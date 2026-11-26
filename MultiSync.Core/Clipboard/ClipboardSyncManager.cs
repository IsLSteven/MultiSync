using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MultiSync.Core.ClipboardSync;

public sealed record FileTransferManifest(
    string TransferId,
    List<FileTransferEntry> Entries,
    long TotalBytes);

public sealed record FileTransferEntry(
    string RelativePath,
    long SizeBytes,
    bool IsDirectory);

/// <summary>
/// Bidirectional Windows clipboard monitor and writer.
/// Uses GetClipboardSequenceNumber() + Win32 OpenClipboard for fast polling, and STA OLE Clipboard
/// (Clipboard.SetDataObject with FileDrop + Preferred DropEffect) so Windows Explorer Ctrl+V works reliably.
/// </summary>
public sealed class ClipboardSyncManager : IDisposable
{
    public const long MaxAutoFileTransferBytes = 2L * 1024 * 1024 * 1024; // 2 GB
    public const int MaxImageBytes = 20 * 1024 * 1024; // 20 MB
    public const int MaxTextChars = 2 * 1024 * 1024;

    private const uint CF_DIB = 8;
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_HDROP = 15;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint GMEM_ZEROINIT = 0x0040;
    private const uint GMEM_DDESHARE = 0x2000;
    private const int OpenRetries = 12;

    private static readonly uint CfPng = RegisterClipboardFormatW("PNG");
    private static readonly uint CfPreferredDropEffect = RegisterClipboardFormatW("Preferred DropEffect");
    private static readonly Lock ClipboardLock = new();

    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private StaClipboardHost? _staHost;
    private Thread? _ownerThread;
    private readonly ManualResetEventSlim _ownerReady = new();

    private uint _lastObservedSequence;
    private uint _lastWrittenSequence;
    private string _lastContentHash = "";

    public event Action<string>? LocalTextCopied;
    public event Action<byte[]>? LocalImageCopied;
    public event Action<List<string>>? LocalFilesCopied;

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();

        // Dedicated STA thread with message pump for Win32 OpenClipboard owner + OLE Clipboard (SetFileDropList)
        _ownerThread = new Thread(() =>
        {
            _staHost = new StaClipboardHost();
            _ = _staHost.Handle; // force HWND creation on STA thread
            _ownerReady.Set();
            Application.Run();
        })
        {
            Name = "MultiSync.ClipboardSTA",
            IsBackground = true,
        };
        _ownerThread.SetApartmentState(ApartmentState.STA);
        _ownerThread.Start();
        _ownerReady.Wait(TimeSpan.FromSeconds(2));

        _lastObservedSequence = GetClipboardSequenceNumber();
        var token = _cts.Token;
        _pollTask = Task.Run(() => PollLoopAsync(token), token);
    }

    /// <summary>Forces an immediate check of the local clipboard (e.g. when switching screens).</summary>
    public void CheckNow()
    {
        try { InspectClipboardIfChanged(forceCheck: false); }
        catch { }
    }

    public bool ApplyRemoteText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var hash = HashString("T:" + text);
        if (hash == _lastContentHash) return true;

        var utf16 = Encoding.Unicode.GetBytes(text + '\0');
        var ok = WriteClipboard(() => SetGlobalData(CF_UNICODETEXT, utf16), hash);
        return ok;
    }

    public bool ApplyRemoteImagePng(byte[] pngBytes)
    {
        if (pngBytes.Length == 0) return false;
        var hash = HashBytes("I:", pngBytes);
        if (hash == _lastContentHash) return true;

        var dibBytes = TryConvertPngToDib(pngBytes);
        return WriteClipboard(() =>
        {
            var wrotePng = CfPng != 0 && SetGlobalData(CfPng, pngBytes);
            var wroteDib = dibBytes != null && SetGlobalData(CF_DIB, dibBytes);
            return wrotePng || wroteDib;
        }, hash);
    }

    public bool ApplyRemoteFiles(IReadOnlyList<string> localPaths)
    {
        var valid = localPaths
            .Where(p => !string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p)))
            .ToList();
        if (valid.Count == 0) return false;

        var hash = HashString("F:" + string.Join("|", valid));

        // Prefer STA OLE Clipboard (OleSetClipboard + OleFlushClipboard) so Windows Explorer always enables Ctrl+V
        if (TryWriteFilesViaStaOle(valid, hash))
            return true;

        // Fallback to pure Win32 CF_HDROP + Preferred DropEffect
        var dropBytes = BuildDropFilesBuffer(valid);
        var copyEffect = BitConverter.GetBytes(1u); // DROPEFFECT_COPY = 1
        return WriteClipboard(() =>
        {
            var okDrop = SetGlobalData(CF_HDROP, dropBytes);
            if (okDrop && CfPreferredDropEffect != 0)
                SetGlobalData(CfPreferredDropEffect, copyEffect);
            return okDrop;
        }, hash);
    }

    private bool TryWriteFilesViaStaOle(List<string> paths, string contentHash)
    {
        var host = _staHost;
        if (host == null || host.IsDisposed || !host.IsHandleCreated)
            return false;

        try
        {
            var ok = false;
            host.Invoke(() =>
            {
                lock (ClipboardLock)
                {
                    var sc = new StringCollection();
                    foreach (var p in paths) sc.Add(p);

                    var dataObj = new DataObject();
                    dataObj.SetFileDropList(sc);
                    // Mark as DROPEFFECT_COPY (1) so Explorer copies instead of moving
                    var effectStream = new MemoryStream(BitConverter.GetBytes(1));
                    dataObj.SetData("Preferred DropEffect", effectStream);

                    System.Windows.Forms.Clipboard.SetDataObject(dataObj, copy: true, retryTimes: 10, retryDelay: 25);
                    _lastContentHash = contentHash;
                    var seq = GetClipboardSequenceNumber();
                    _lastWrittenSequence = seq;
                    _lastObservedSequence = seq;
                    ok = true;
                }
            });
            return ok;
        }
        catch
        {
            return false;
        }
    }

    private async Task PollLoopAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                InspectClipboardIfChanged(forceCheck: false);
            }
            catch
            {
                // ignore transient clipboard lock
            }

            try { await Task.Delay(120, cancel); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void InspectClipboardIfChanged(bool forceCheck)
    {
        var seq = GetClipboardSequenceNumber();
        if (!forceCheck && (seq == _lastObservedSequence || seq == _lastWrittenSequence))
            return;

        _lastObservedSequence = seq;

        List<string>? files = null;
        string? text = null;
        byte[]? pngBytes = null;

        lock (ClipboardLock)
        {
            if (!TryOpenClipboard(OwnerHwnd))
                return;

            try
            {
                if (IsClipboardFormatAvailable(CF_HDROP))
                {
                    files = ReadDropFilesFromOpenClipboard();
                }
                else if (IsClipboardFormatAvailable(CF_UNICODETEXT))
                {
                    text = ReadUnicodeTextFromOpenClipboard();
                }
                else if (CfPng != 0 && IsClipboardFormatAvailable(CfPng))
                {
                    pngBytes = ReadGlobalMemory(GetClipboardData(CfPng));
                }
                else if (IsClipboardFormatAvailable(CF_DIB))
                {
                    var dib = ReadGlobalMemory(GetClipboardData(CF_DIB));
                    if (dib != null)
                        pngBytes = TryConvertDibToPng(dib);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }

        if (files is { Count: > 0 })
        {
            var stagingRoot = GetStagingRoot();
            if (files.All(p => p.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase)))
                return;

            // Always forward when user explicitly presses Ctrl+C on files (even if re-copying the same file)
            _lastContentHash = HashString("F:" + string.Join("|", files));
            LocalFilesCopied?.Invoke(files);
            return;
        }

        if (!string.IsNullOrEmpty(text) && text.Length <= MaxTextChars)
        {
            var hash = HashString("T:" + text);
            if (hash == _lastContentHash) return;
            _lastContentHash = hash;
            LocalTextCopied?.Invoke(text);
            return;
        }

        if (pngBytes is { Length: > 0 and <= MaxImageBytes })
        {
            var hash = HashBytes("I:", pngBytes);
            if (hash == _lastContentHash) return;
            _lastContentHash = hash;
            LocalImageCopied?.Invoke(pngBytes);
        }
    }

    private bool WriteClipboard(Func<bool> writer, string contentHash)
    {
        lock (ClipboardLock)
        {
            if (!TryOpenClipboard(OwnerHwnd))
                return false;

            try
            {
                if (!EmptyClipboard())
                    return false;

                var ok = writer();
                if (ok)
                    _lastContentHash = contentHash;
                return ok;
            }
            finally
            {
                CloseClipboard();
                var seq = GetClipboardSequenceNumber();
                _lastWrittenSequence = seq;
                _lastObservedSequence = seq;
            }
        }
    }

    private nint OwnerHwnd => _staHost is { IsDisposed: false, IsHandleCreated: true } ? _staHost.Handle : nint.Zero;

    private static bool TryOpenClipboard(nint owner)
    {
        for (var i = 0; i < OpenRetries; i++)
        {
            if (OpenClipboard(owner))
                return true;
            Thread.Sleep(15);
        }
        return false;
    }

    private static string? ReadUnicodeTextFromOpenClipboard()
    {
        var hMem = GetClipboardData(CF_UNICODETEXT);
        if (hMem == nint.Zero) return null;
        var ptr = GlobalLock(hMem);
        if (ptr == nint.Zero) return null;
        try
        {
            return Marshal.PtrToStringUni(ptr);
        }
        finally
        {
            GlobalUnlock(hMem);
        }
    }

    private static unsafe List<string> ReadDropFilesFromOpenClipboard()
    {
        var list = new List<string>();
        var hDrop = GetClipboardData(CF_HDROP);
        if (hDrop == nint.Zero) return list;

        var count = DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
        if (count == 0 || count > 2048) return list;

        for (uint i = 0; i < count; i++)
        {
            var chars = DragQueryFileW(hDrop, i, null, 0);
            if (chars == 0) continue;
            var buf = new char[chars + 1];
            fixed (char* p = buf)
            {
                var written = DragQueryFileW(hDrop, i, p, (uint)buf.Length);
                if (written > 0)
                {
                    var path = new string(buf, 0, (int)written);
                    if (File.Exists(path) || Directory.Exists(path))
                        list.Add(path);
                }
            }
        }
        return list;
    }

    /// <summary>
    /// Builds a valid Win32 DROPFILES (CF_HDROP) memory block: 20-byte header + UTF-16LE null-terminated paths + final NUL.
    /// </summary>
    public static byte[] BuildDropFilesBuffer(IReadOnlyList<string> paths)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.Unicode, leaveOpen: true);
        bw.Write(20); // pFiles offset
        bw.Write(0);  // pt.x
        bw.Write(0);  // pt.y
        bw.Write(0);  // fNC = FALSE
        bw.Write(1);  // fWide = TRUE (UTF-16LE)
        foreach (var path in paths)
        {
            bw.Write(Encoding.Unicode.GetBytes(path));
            bw.Write((short)0);
        }
        bw.Write((short)0); // double-NUL terminator
        return ms.ToArray();
    }

    private static byte[]? ReadGlobalMemory(nint hMem)
    {
        if (hMem == nint.Zero) return null;
        var size = (int)GlobalSize(hMem);
        if (size <= 0 || size > MaxImageBytes) return null;
        var ptr = GlobalLock(hMem);
        if (ptr == nint.Zero) return null;
        try
        {
            var data = new byte[size];
            Marshal.Copy(ptr, data, 0, size);
            return data;
        }
        finally
        {
            GlobalUnlock(hMem);
        }
    }

    private static bool SetGlobalData(uint format, byte[] data)
    {
        var hMem = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT | GMEM_DDESHARE, (nuint)data.Length);
        if (hMem == nint.Zero) return false;
        var ptr = GlobalLock(hMem);
        if (ptr == nint.Zero)
        {
            GlobalFree(hMem);
            return false;
        }
        try
        {
            Marshal.Copy(data, 0, ptr, data.Length);
        }
        finally
        {
            GlobalUnlock(hMem);
        }

        if (SetClipboardData(format, hMem) != nint.Zero)
            return true;

        GlobalFree(hMem);
        return false;
    }

    private static byte[]? TryConvertDibToPng(byte[] dib)
    {
        try
        {
            if (dib.Length < 40) return null;
            var headerSize = BitConverter.ToInt32(dib, 0);
            var bpp = BitConverter.ToInt16(dib, 14);
            var compression = BitConverter.ToInt32(dib, 16);
            var clrUsed = BitConverter.ToInt32(dib, 32);

            var paletteBytes = clrUsed > 0 ? clrUsed * 4 : (bpp <= 8 ? (1 << bpp) * 4 : 0);
            var maskBytes = (headerSize == 40 && compression == 3) ? 12 : 0;
            var pixelOffset = 14 + headerSize + paletteBytes + maskBytes;

            var bmpFile = new byte[14 + dib.Length];
            bmpFile[0] = (byte)'B';
            bmpFile[1] = (byte)'M';
            BitConverter.TryWriteBytes(bmpFile.AsSpan(2, 4), bmpFile.Length);
            BitConverter.TryWriteBytes(bmpFile.AsSpan(10, 4), pixelOffset);
            Buffer.BlockCopy(dib, 0, bmpFile, 14, dib.Length);

            using var inMs = new MemoryStream(bmpFile);
            using var img = Image.FromStream(inMs);
            using var outMs = new MemoryStream();
            img.Save(outMs, ImageFormat.Png);
            return outMs.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? TryConvertPngToDib(byte[] png)
    {
        try
        {
            using var inMs = new MemoryStream(png);
            using var img = Image.FromStream(inMs);
            using var bmpMs = new MemoryStream();
            img.Save(bmpMs, ImageFormat.Bmp);
            var bmpBytes = bmpMs.ToArray();
            if (bmpBytes.Length <= 14) return null;
            // Strip 14-byte BITMAPFILEHEADER to get CF_DIB
            var dib = new byte[bmpBytes.Length - 14];
            Buffer.BlockCopy(bmpBytes, 14, dib, 0, dib.Length);
            return dib;
        }
        catch
        {
            return null;
        }
    }

    public static (FileTransferManifest Manifest, Dictionary<string, string> FullPathByRelative)? BuildFileManifest(
        IReadOnlyList<string> sourcePaths)
    {
        var entries = new List<FileTransferEntry>();
        var fullPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;

        foreach (var rawPath in sourcePaths)
        {
            if (File.Exists(rawPath))
            {
                var fi = new FileInfo(rawPath);
                totalBytes += fi.Length;
                if (totalBytes > MaxAutoFileTransferBytes)
                    return null;

                var rel = fi.Name;
                entries.Add(new FileTransferEntry(rel, fi.Length, false));
                fullPaths[rel] = fi.FullName;
            }
            else if (Directory.Exists(rawPath))
            {
                var rootDir = new DirectoryInfo(rawPath);
                var baseName = rootDir.Name;
                entries.Add(new FileTransferEntry(baseName, 0, true));

                foreach (var dir in rootDir.EnumerateDirectories("*", SearchOption.AllDirectories))
                {
                    var relDir = Path.Combine(baseName, Path.GetRelativePath(rootDir.FullName, dir.FullName));
                    entries.Add(new FileTransferEntry(relDir, 0, true));
                }

                foreach (var fi in rootDir.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    totalBytes += fi.Length;
                    if (totalBytes > MaxAutoFileTransferBytes)
                        return null;

                    var relFile = Path.Combine(baseName, Path.GetRelativePath(rootDir.FullName, fi.FullName));
                    entries.Add(new FileTransferEntry(relFile, fi.Length, false));
                    fullPaths[relFile] = fi.FullName;
                }
            }
        }

        if (entries.Count == 0) return null;
        var manifest = new FileTransferManifest(Guid.NewGuid().ToString("N")[..10], entries, totalBytes);
        return (manifest, fullPaths);
    }

    public static string GetStagingRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "MultiSync", "Received");
        Directory.CreateDirectory(root);
        return root;
    }

    public static void CleanupOldStagingFolders()
    {
        try
        {
            var root = GetStagingRoot();
            var cutoff = DateTime.UtcNow.AddHours(-4);
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (Directory.GetCreationTimeUtc(dir) < cutoff)
                {
                    try { Directory.Delete(dir, recursive: true); } catch { }
                }
            }
        }
        catch { }
    }

    private static string HashString(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string HashBytes(string prefix, byte[] data)
    {
        var pfx = Encoding.UTF8.GetBytes(prefix);
        var combined = new byte[pfx.Length + data.Length];
        Buffer.BlockCopy(pfx, 0, combined, 0, pfx.Length);
        Buffer.BlockCopy(data, 0, combined, pfx.Length, data.Length);
        return Convert.ToHexString(SHA256.HashData(combined));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _pollTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cts?.Dispose();
        try
        {
            if (_staHost is { IsDisposed: false, IsHandleCreated: true })
                _staHost.BeginInvoke(() => { _staHost.Dispose(); Application.ExitThread(); });
        }
        catch { }
        _ownerReady.Dispose();
    }

    private sealed class StaClipboardHost : Control
    {
        public StaClipboardHost()
        {
            Visible = false;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Parent = new nint(-3); // HWND_MESSAGE
                return cp;
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint uFormat, nint hMem);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalFree(nint hMem);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint hMem);

    [DllImport("kernel32.dll")]
    private static extern nuint GlobalSize(nint hMem);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern unsafe uint DragQueryFileW(nint hDrop, uint iFile, char* lpszFile, uint cch);
}
