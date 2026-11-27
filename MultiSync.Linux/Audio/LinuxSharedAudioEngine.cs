using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace MultiSync.Linux.Audio;

public enum AudioShareMode { Off, ReceiveHere, SendToPeer }

/// <summary>
/// Wire-compatible Linux Shared Audio engine (UDP 5053) using PulseAudio / PipeWire (`parec` & `paplay`).
/// Interoperates seamlessly with Windows WASAPI SharedAudioEngine using identical 48 kHz 16-bit Stereo 10 ms HMAC packets.
/// </summary>
public sealed class LinuxSharedAudioEngine : IDisposable
{
    public const int DefaultAudioPort = 5053;
    private const int FrameBytes = 1920; // 10 ms @ 48 kHz 16-bit Stereo
    private const int HeaderBytes = 12;  // [4B seq][8B HMAC-SHA256 truncated tag]

    private readonly Lock _sync = new();
    private CancellationTokenSource? _cts;
    private UdpClient? _udp;
    private Process? _audioProc;
    private byte[] _authKey = [];
    private uint _sendSeq;
    private AudioShareMode _mode = AudioShareMode.Off;
    private string? _remoteIp;
    private float _volume = 1.0f;
    private bool _muted;

    public event Action? StatusChanged;

    public AudioShareMode Mode => _mode;
    public string? RemoteEndpointIp => _remoteIp;
    public float Volume { get => _volume; set => _volume = Math.Clamp(value, 0f, 1.5f); }
    public bool IsMuted { get => _muted; set { _muted = value; StatusChanged?.Invoke(); } }

    public void StartReceiving(string sessionKey, int port = DefaultAudioPort)
    {
        lock (_sync)
        {
            StopInternal();
            _authKey = SHA256.HashData(Encoding.UTF8.GetBytes("multisync-audio:" + sessionKey));
            _mode = AudioShareMode.ReceiveHere;
            _remoteIp = null;
            _cts = new CancellationTokenSource();

            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.ReceiveBufferSize = 256 * 1024;
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));

            _audioProc = Process.Start(new ProcessStartInfo(
                "paplay",
                "--raw --format=s16le --rate=48000 --channels=2 --latency-msec=25 --client-name=MultiSync")
            {
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            var udp = _udp;
            var stdin = _audioProc?.StandardInput.BaseStream;
            var token = _cts.Token;
            _ = Task.Run(() => ReceiveLoopAsync(udp, stdin, token), token);
        }
        StatusChanged?.Invoke();
    }

    public void StartSending(string targetIp, string sessionKey, int port = DefaultAudioPort)
    {
        lock (_sync)
        {
            StopInternal();
            _authKey = SHA256.HashData(Encoding.UTF8.GetBytes("multisync-audio:" + sessionKey));
            _mode = AudioShareMode.SendToPeer;
            _remoteIp = targetIp;
            _sendSeq = 0;
            _cts = new CancellationTokenSource();

            _udp = new UdpClient();
            _udp.Client.SendBufferSize = 256 * 1024;
            var dest = new IPEndPoint(IPAddress.Parse(targetIp), port);

            // @DEFAULT_MONITOR@ works seamlessly on PipeWire (pipewire-pulse) and PulseAudio across Debian/Mint/Ubuntu/Arch
            _audioProc = Process.Start(new ProcessStartInfo(
                "parec",
                "--format=s16le --rate=48000 --channels=2 --latency-msec=15 -d @DEFAULT_MONITOR@ --client-name=MultiSync")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            var udp = _udp;
            var stdout = _audioProc?.StandardOutput.BaseStream;
            var token = _cts.Token;
            _ = Task.Run(() => SendLoopAsync(udp, dest, stdout, token), token);
        }
        StatusChanged?.Invoke();
    }

    public void Stop()
    {
        lock (_sync) StopInternal();
        StatusChanged?.Invoke();
    }

    private async Task SendLoopAsync(UdpClient udp, IPEndPoint dest, Stream? stdout, CancellationToken cancel)
    {
        if (stdout == null) return;
        var pcm = new byte[FrameBytes];
        var packet = new byte[HeaderBytes + FrameBytes];
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                await stdout.ReadExactlyAsync(pcm, cancel);
                var seq = Interlocked.Increment(ref _sendSeq);
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(0, 4), seq);
                Buffer.BlockCopy(pcm, 0, packet, HeaderBytes, FrameBytes);

                var tag = ComputeTag(seq, pcm);
                tag.AsSpan(0, 8).CopyTo(packet.AsSpan(4, 8));
                await udp.SendAsync(packet, dest, cancel);
            }
        }
        catch { }
    }

    private async Task ReceiveLoopAsync(UdpClient udp, Stream? stdin, CancellationToken cancel)
    {
        if (stdin == null) return;
        uint lastSeq = 0;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                var res = await udp.ReceiveAsync(cancel);
                var data = res.Buffer;
                if (data.Length <= HeaderBytes) continue;

                var seq = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4));
                var pcm = data.AsSpan(HeaderBytes);
                var expected = ComputeTag(seq, pcm);
                if (!CryptographicOperations.FixedTimeEquals(expected.AsSpan(0, 8), data.AsSpan(4, 8)))
                    continue;
                if (lastSeq != 0 && unchecked((int)(seq - lastSeq)) < -5)
                    continue;
                lastSeq = seq;

                var gain = _muted ? 0f : _volume;
                if (Math.Abs(gain - 1.0f) < 0.01f)
                {
                    await stdin.WriteAsync(data.AsMemory(HeaderBytes), cancel);
                }
                else
                {
                    var scaled = new byte[pcm.Length];
                    for (var i = 0; i + 1 < pcm.Length; i += 2)
                    {
                        var sample = BitConverter.ToInt16(pcm.Slice(i, 2));
                        var outSample = (short)Math.Clamp((int)(sample * gain), short.MinValue, short.MaxValue);
                        BitConverter.TryWriteBytes(scaled.AsSpan(i, 2), outSample);
                    }
                    await stdin.WriteAsync(scaled, cancel);
                }
            }
        }
        catch { }
    }

    public static byte[] ComputeAudioTag(byte[] authKey, uint seq, ReadOnlySpan<byte> pcm)
    {
        Span<byte> seqBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(seqBuf, seq);
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, authKey);
        hmac.AppendData(seqBuf);
        hmac.AppendData(pcm);
        return hmac.GetCurrentHash();
    }

    private byte[] ComputeTag(uint seq, ReadOnlySpan<byte> pcm) => ComputeAudioTag(_authKey, seq, pcm);

    private void StopInternal()
    {
        _mode = AudioShareMode.Off;
        _cts?.Cancel();
        try { if (_audioProc is { HasExited: false }) _audioProc.Kill(entireProcessTree: true); } catch { }
        try { _audioProc?.Dispose(); } catch { }
        _audioProc = null;
        try { _udp?.Dispose(); } catch { }
        _udp = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        lock (_sync) StopInternal();
    }
}
