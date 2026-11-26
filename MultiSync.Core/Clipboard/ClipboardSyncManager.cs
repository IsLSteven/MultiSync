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
/// Watches the Windows clipboard in real time via AddClipboardFormatListener (WM_CLIPBOARDUPDATE)
/// and handles bidirectional sync of text, images (screenshots), and files/folders (CF_HDROP).
/// </summary>
public sealed class ClipboardSyncManager : IDisposable
{
    public const long MaxAutoFileTransferBytes = 512L * 1024 * 1024; // 512 MB
    public const int MaxImageBytes = 20 * 1024 * 1024; // 20 MB
    public const int MaxTextChars = 2 * 1024 * 1024;

    private Thread? _staThread;
    private ClipboardListenerWindow? _window;
    private readonly ManualResetEventSlim _ready = new();

    private string _lastContentHash = "";
    private long _suppressUpdatesUntilTick;

    public event Action<string>? LocalTextCopied;
    public event Action<byte[]>? LocalImageCopied;
    public event Action<List<string>>? LocalFilesCopied;

    public void Start()
    {
        if (_staThread != null) return;
        _staThread = new Thread(() =>
        {
            _window = new ClipboardListenerWindow(OnClipboardChanged);
            _ = _window.Handle;
            _ready.Set();
            Application.Run(_window);
        })
        {
            Name = "MultiSync.ClipboardWatcher",
            IsBackground = true,
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
        _ready.Wait(TimeSpan.FromSeconds(3));
    }

    public void ApplyRemoteText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var hash = HashString("T:" + text);
        if (hash == _lastContentHash) return;

        RunOnSta(() =>
        {
            _lastContentHash = hash;
            _suppressUpdatesUntilTick = Environment.TickCount64 + 600;
            TryWithRetry(() => Clipboard.SetText(text));
        });
    }

    public void ApplyRemoteImagePng(byte[] pngBytes)
    {
        if (pngBytes.Length == 0) return;
        var hash = HashBytes("I:", pngBytes);
        if (hash == _lastContentHash) return;

        RunOnSta(() =>
        {
            _lastContentHash = hash;
            _suppressUpdatesUntilTick = Environment.TickCount64 + 600;
            TryWithRetry(() =>
            {
                using var ms = new MemoryStream(pngBytes);
                using var img = Image.FromStream(ms);
                Clipboard.SetImage(new Bitmap(img));
            });
        });
    }

    public void ApplyRemoteFiles(IReadOnlyList<string> localPaths)
    {
        if (localPaths.Count == 0) return;
        var sig = string.Join("|", localPaths);
        var hash = HashString("F:" + sig);

        RunOnSta(() =>
        {
            _lastContentHash = hash;
            _suppressUpdatesUntilTick = Environment.TickCount64 + 800;
            TryWithRetry(() =>
            {
                var col = new StringCollection();
                foreach (var p in localPaths)
                    if (File.Exists(p) || Directory.Exists(p))
                        col.Add(p);
                if (col.Count > 0)
                    Clipboard.SetFileDropList(col);
            });
        });
    }

    private void OnClipboardChanged()
    {
        if (Environment.TickCount64 <= _suppressUpdatesUntilTick)
            return;

        try
        {
            // 1. Files / Folders (CF_HDROP)
            if (Clipboard.ContainsFileDropList())
            {
                var dropList = Clipboard.GetFileDropList();
                var paths = new List<string>();
                foreach (var p in dropList)
                {
                    if (!string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p)))
                        paths.Add(p);
                }

                if (paths.Count > 0)
                {
                    // Skip files inside our own receive staging folder so we don't bounce them back
                    var stagingRoot = GetStagingRoot();
                    if (paths.All(p => p.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase)))
                        return;

                    var hash = HashString("F:" + string.Join("|", paths));
                    if (hash == _lastContentHash) return;
                    _lastContentHash = hash;
                    LocalFilesCopied?.Invoke(paths);
                    return;
                }
            }

            // 2. Text
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (!string.IsNullOrEmpty(text) && text.Length <= MaxTextChars)
                {
                    var hash = HashString("T:" + text);
                    if (hash == _lastContentHash) return;
                    _lastContentHash = hash;
                    LocalTextCopied?.Invoke(text);
                    return;
                }
            }

            // 3. Images (e.g. Win+Shift+S, PrintScreen, Copy Image)
            if (Clipboard.ContainsImage())
            {
                using var img = Clipboard.GetImage();
                if (img != null)
                {
                    using var ms = new MemoryStream();
                    img.Save(ms, ImageFormat.Png);
                    var bytes = ms.ToArray();
                    if (bytes.Length > 0 && bytes.Length <= MaxImageBytes)
                    {
                        var hash = HashBytes("I:", bytes);
                        if (hash == _lastContentHash) return;
                        _lastContentHash = hash;
                        LocalImageCopied?.Invoke(bytes);
                    }
                }
            }
        }
        catch
        {
            // Clipboard temporarily locked by another app — ignore cleanly
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

    private void RunOnSta(Action action)
    {
        var win = _window;
        if (win == null || win.IsDisposed || !win.IsHandleCreated) return;
        try { win.BeginInvoke(action); } catch { }
    }

    private static void TryWithRetry(Action action)
    {
        for (var i = 0; i < 4; i++)
        {
            try
            {
                action();
                return;
            }
            catch
            {
                Thread.Sleep(25);
            }
        }
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
        var win = _window;
        if (win != null && !win.IsDisposed && win.IsHandleCreated)
        {
            try { win.BeginInvoke(() => win.Close()); } catch { }
        }
        _staThread?.Join(TimeSpan.FromSeconds(1));
        _ready.Dispose();
    }

    private sealed class ClipboardListenerWindow : Form
    {
        private const int WM_CLIPBOARDUPDATE = 0x031D;
        private readonly Action _onChanged;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(nint hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListener(nint hwnd);

        public ClipboardListenerWindow(Action onChanged)
        {
            _onChanged = onChanged;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Minimized;
            Opacity = 0;
            Size = new Size(1, 1);
        }

        protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            AddClipboardFormatListener(Handle);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            RemoveClipboardFormatListener(Handle);
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE)
                _onChanged();
            base.WndProc(ref m);
        }
    }
}
