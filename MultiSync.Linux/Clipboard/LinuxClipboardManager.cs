using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MultiSync.Linux.Protocol;

namespace MultiSync.Linux.Clipboard;

/// <summary>
/// Cross-desktop Linux clipboard monitor & writer supporting BOTH Wayland (wl-copy/wl-paste)
/// and X11 (xclip) across Debian, Linux Mint, Ubuntu, and Arch Linux.
/// Supports UTF-8 text, PNG images, and copied files/folders (text/uri-list).
/// </summary>
public sealed class LinuxClipboardManager : IDisposable
{
    public const long MaxAutoFileTransferBytes = 2L * 1024 * 1024 * 1024;
    private readonly bool _useWayland;
    private readonly bool _hasTool;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private string _lastHash = "";
    private string _lastAppliedHash = "";

    public event Action<string>? LocalTextCopied;
    public event Action<byte[]>? LocalImageCopied;
    public event Action<List<string>>? LocalFilesCopied;

    public LinuxClipboardManager()
    {
        _useWayland = SystemInfo.IsWayland && ProcessRunner.CommandExists("wl-paste") && ProcessRunner.CommandExists("wl-copy");
        _hasTool = _useWayland || ProcessRunner.CommandExists("xclip");
    }

    public void Start()
    {
        if (!_hasTool || _cts != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _pollTask = Task.Run(() => PollLoopAsync(token), token);
    }

    public bool ApplyRemoteText(string text)
    {
        if (!_hasTool || string.IsNullOrEmpty(text)) return false;
        var hash = Hash("T:" + text);
        if (hash == _lastHash) return true;
        _lastAppliedHash = hash;
        _lastHash = hash;
        return WriteClipboard("text/plain;charset=utf-8", Encoding.UTF8.GetBytes(text));
    }

    public bool ApplyRemoteImagePng(byte[] pngBytes)
    {
        if (!_hasTool || pngBytes.Length == 0) return false;
        var hash = HashBytes("I:", pngBytes);
        if (hash == _lastHash) return true;
        _lastAppliedHash = hash;
        _lastHash = hash;
        return WriteClipboard("image/png", pngBytes);
    }

    public bool ApplyRemoteFiles(IReadOnlyList<string> paths)
    {
        if (!_hasTool || paths.Count == 0) return false;
        var valid = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (valid.Count == 0) return false;

        var hash = Hash("F:" + string.Join("|", valid));
        _lastAppliedHash = hash;
        _lastHash = hash;

        // Standard x-special/gnome-copied-files format works in Nautilus (Ubuntu), Nemo (Mint), Thunar, Dolphin (Arch), PCManFM (Debian)
        var sb = new StringBuilder("copy\n");
        foreach (var p in valid)
            sb.AppendLine(new Uri(Path.GetFullPath(p)).AbsoluteUri);

        return WriteClipboard("x-special/gnome-copied-files", Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private async Task PollLoopAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                InspectOnce();
            }
            catch { }

            try { await Task.Delay(350, cancel); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void InspectOnce()
    {
        var targets = ReadTargets();
        if (string.IsNullOrWhiteSpace(targets)) return;

        // 1. Check files first (text/uri-list)
        if (targets.Contains("text/uri-list", StringComparison.OrdinalIgnoreCase))
        {
            var raw = ReadClipboardBytes("text/uri-list");
            if (raw is { Length: > 0 })
            {
                var paths = ParseUriList(Encoding.UTF8.GetString(raw));
                var stagingRoot = GetStagingRoot();
                if (paths.Count > 0 && !paths.All(p => p.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase)))
                {
                    var hash = Hash("F:" + string.Join("|", paths));
                    if (hash != _lastHash && hash != _lastAppliedHash)
                    {
                        _lastHash = hash;
                        LocalFilesCopied?.Invoke(paths);
                    }
                    return;
                }
            }
        }

        // 2. Check text
        if (targets.Contains("UTF8_STRING", StringComparison.OrdinalIgnoreCase) ||
            targets.Contains("text/plain", StringComparison.OrdinalIgnoreCase))
        {
            var raw = ReadClipboardBytes(_useWayland ? "text/plain" : "UTF8_STRING");
            if (raw is { Length: > 0 and <= 4 * 1024 * 1024 })
            {
                var text = Encoding.UTF8.GetString(raw);
                var hash = Hash("T:" + text);
                if (hash != _lastHash && hash != _lastAppliedHash)
                {
                    _lastHash = hash;
                    LocalTextCopied?.Invoke(text);
                }
                return;
            }
        }

        // 3. Check PNG image
        if (targets.Contains("image/png", StringComparison.OrdinalIgnoreCase))
        {
            var png = ReadClipboardBytes("image/png");
            if (png is { Length: > 0 and <= 20 * 1024 * 1024 })
            {
                var hash = HashBytes("I:", png);
                if (hash != _lastHash && hash != _lastAppliedHash)
                {
                    _lastHash = hash;
                    LocalImageCopied?.Invoke(png);
                }
            }
        }
    }

    private string? ReadTargets() =>
        _useWayland
            ? ProcessRunner.RunCapture("wl-paste", "--list-types", 800)
            : ProcessRunner.RunCapture("xclip", "-selection clipboard -o -t TARGETS", 800);

    private byte[]? ReadClipboardBytes(string mime)
    {
        try
        {
            var psi = _useWayland
                ? new ProcessStartInfo("wl-paste", $"--no-newline --type {mime}")
                : new ProcessStartInfo("xclip", $"-selection clipboard -o -t {mime}");
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using var p = Process.Start(psi);
            if (p == null) return null;
            using var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            p.WaitForExit(1500);
            return p.ExitCode == 0 ? ms.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }

    private bool WriteClipboard(string mime, byte[] data)
    {
        try
        {
            var psi = _useWayland
                ? new ProcessStartInfo("wl-copy", $"--type {mime}")
                : new ProcessStartInfo("xclip", $"-selection clipboard -i -t {mime}");
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.StandardInput.BaseStream.Write(data, 0, data.Length);
            p.StandardInput.BaseStream.Flush();
            p.StandardInput.Close();
            if (_useWayland) p.WaitForExit(800); // xclip forks to hold X11 selection; don't block
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static List<string> ParseUriList(string uriList)
    {
        var list = new List<string>();
        foreach (var rawLine in uriList.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('#') || !line.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) continue;
            if (Uri.TryCreate(line, UriKind.Absolute, out var uri))
            {
                var path = Uri.UnescapeDataString(uri.LocalPath);
                if (File.Exists(path) || Directory.Exists(path))
                    list.Add(path);
            }
        }
        return list;
    }

    public static (FileTransferManifest Manifest, Dictionary<string, string> FullPathByRelative)? BuildFileManifest(IReadOnlyList<string> sourcePaths)
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
                if (totalBytes > MaxAutoFileTransferBytes) return null;
                entries.Add(new FileTransferEntry(fi.Name, fi.Length, false));
                fullPaths[fi.Name] = fi.FullName;
            }
            else if (Directory.Exists(rawPath))
            {
                var rootDir = new DirectoryInfo(rawPath);
                var baseName = rootDir.Name;
                entries.Add(new FileTransferEntry(baseName, 0, true));
                foreach (var dir in rootDir.EnumerateDirectories("*", SearchOption.AllDirectories))
                    entries.Add(new FileTransferEntry(Path.Combine(baseName, Path.GetRelativePath(rootDir.FullName, dir.FullName)), 0, true));
                foreach (var fi in rootDir.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    totalBytes += fi.Length;
                    if (totalBytes > MaxAutoFileTransferBytes) return null;
                    var rel = Path.Combine(baseName, Path.GetRelativePath(rootDir.FullName, fi.FullName));
                    entries.Add(new FileTransferEntry(rel, fi.Length, false));
                    fullPaths[rel] = fi.FullName;
                }
            }
        }

        if (entries.Count == 0) return null;
        return (new FileTransferManifest(Guid.NewGuid().ToString("N")[..10], entries, totalBytes), fullPaths);
    }

    public static string GetStagingRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(home, "Downloads", "MultiSync");
        Directory.CreateDirectory(downloads);
        return downloads;
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private static string HashBytes(string pfx, byte[] data)
    {
        var pb = Encoding.UTF8.GetBytes(pfx);
        var buf = new byte[pb.Length + data.Length];
        Buffer.BlockCopy(pb, 0, buf, 0, pb.Length);
        Buffer.BlockCopy(data, 0, buf, pb.Length, data.Length);
        return Convert.ToHexString(SHA256.HashData(buf));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _pollTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cts?.Dispose();
    }
}
