using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MultiSync.Core.Audio;

public enum AudioShareMode
{
    Off,
    ReceiveHere, // This PC has the headset and plays audio received from remote PC
    SendToPeer,  // This PC captures system audio via WASAPI Loopback and streams it to remote PC
}

/// <summary>
/// Real-time low-latency LAN audio streaming engine (WASAPI Loopback -> UDP -> WASAPI Shared Render).
/// Uses 48 kHz 16-bit Stereo PCM in 10 ms packets (~1,920 bytes/packet) authenticated with an 8-byte HMAC tag
/// derived from the cluster SessionKey, with bounded jitter buffering (~35 ms target) so audio never drifts.
/// </summary>
public sealed class SharedAudioEngine : IDisposable
{
    public const int DefaultAudioPort = 5053;
    private const int TargetSampleRate = 48000;
    private const int TargetChannels = 2;
    private const int BytesPerSample = 2; // 16-bit PCM
    private const int FrameBytes = (TargetSampleRate / 100) * TargetChannels * BytesPerSample; // 10 ms = 1920 bytes
    private const int HeaderBytes = 12; // [4B seq][8B HMAC-SHA256 truncated tag]

    private readonly Lock _sync = new();
    private CancellationTokenSource? _cts;
    private UdpClient? _udp;
    private WasapiLoopbackCapture? _capture;
    private WasapiOut? _player;
    private BufferedWaveProvider? _buffer;
    private VolumeSampleProvider? _volumeProvider;

    private AudioShareMode _mode = AudioShareMode.Off;
    private string? _targetIp;
    private int _port = DefaultAudioPort;
    private byte[] _authKey = [];
    private float _volume = 1.0f;
    private bool _muted;
    private uint _sendSeq;
    private long _lastPacketReceivedTick;
    private long _lastAudioFrameTick;
    private float _peakLevel;

    public event Action? StatusChanged;

    public AudioShareMode Mode => _mode;
    public string? RemoteEndpointIp => _targetIp;
    public bool IsReceivingActive => _mode == AudioShareMode.ReceiveHere && (Environment.TickCount64 - _lastPacketReceivedTick) < 1500;
    public float PeakLevel => (Environment.TickCount64 - _lastAudioFrameTick) > 350 ? 0f : _peakLevel;
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1.5f);
            ApplyVolume();
        }
    }

    public bool IsMuted
    {
        get => _muted;
        set
        {
            _muted = value;
            ApplyVolume();
            StatusChanged?.Invoke();
        }
    }

    public void StartReceiving(string sessionKey, int port = DefaultAudioPort)
    {
        lock (_sync)
        {
            StopInternal();
            _authKey = SHA256.HashData(Encoding.UTF8.GetBytes("multisync-audio:" + sessionKey));
            _port = port;
            _mode = AudioShareMode.ReceiveHere;
            _targetIp = null;
            _cts = new CancellationTokenSource();

            var waveFormat = new WaveFormat(TargetSampleRate, 16, TargetChannels);
            _buffer = new BufferedWaveProvider(waveFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(250),
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };

            _volumeProvider = new VolumeSampleProvider(_buffer.ToSampleProvider())
            {
                Volume = _muted ? 0f : _volume,
            };

            _player = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 30);
            _player.Init(_volumeProvider);
            _player.Play();

            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.ReceiveBufferSize = 256 * 1024;
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _port));

            var token = _cts.Token;
            var udp = _udp;
            _ = Task.Run(() => ReceiveLoopAsync(udp, token), token);
        }
        StatusChanged?.Invoke();
    }

    public void StartSending(string targetIp, string sessionKey, int port = DefaultAudioPort)
    {
        lock (_sync)
        {
            StopInternal();
            _authKey = SHA256.HashData(Encoding.UTF8.GetBytes("multisync-audio:" + sessionKey));
            _targetIp = targetIp;
            _port = port;
            _mode = AudioShareMode.SendToPeer;
            _sendSeq = 0;
            _cts = new CancellationTokenSource();

            _udp = new UdpClient();
            _udp.Client.SendBufferSize = 256 * 1024;
            var dest = new IPEndPoint(IPAddress.Parse(targetIp), _port);

            _capture = new WasapiLoopbackCapture();
            var srcFormat = _capture.WaveFormat;

            // Keep a continuous resampler/converter pipeline across callbacks so 44.1 kHz / 96 kHz
            // sound cards have zero boundary clicks or pops.
            var captureFeed = new BufferedWaveProvider(srcFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(250),
                DiscardOnBufferOverflow = true,
                ReadFully = false,
            };
            var pipeline = Build48kStereo16Pipeline(captureFeed, srcFormat);
            var readBuf = new byte[FrameBytes * 4];
            var pcmAccumulator = new MemoryStream(FrameBytes * 4);

            _capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0 || _mode != AudioShareMode.SendToPeer) return;
                try
                {
                    captureFeed.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    int read;
                    while ((read = pipeline.Read(readBuf, 0, readBuf.Length)) > 0)
                    {
                        _peakLevel = ComputePeak16(readBuf.AsSpan(0, read));
                        _lastAudioFrameTick = Environment.TickCount64;
                        pcmAccumulator.Write(readBuf, 0, read);

                        if (pcmAccumulator.Length >= FrameBytes)
                        {
                            var all = pcmAccumulator.ToArray();
                            var offset = 0;
                            while (offset + FrameBytes <= all.Length)
                            {
                                SendAuthenticatedFrame(dest, all.AsSpan(offset, FrameBytes));
                                offset += FrameBytes;
                            }
                            pcmAccumulator.SetLength(0);
                            if (offset < all.Length)
                                pcmAccumulator.Write(all, offset, all.Length - offset);
                        }
                    }
                }
                catch
                {
                    // ignore transient capture errors
                }
            };

            _capture.StartRecording();
        }
        StatusChanged?.Invoke();
    }

    public void Stop()
    {
        lock (_sync) StopInternal();
        StatusChanged?.Invoke();
    }

    private void SendAuthenticatedFrame(IPEndPoint dest, ReadOnlySpan<byte> pcmFrame)
    {
        var udp = _udp;
        if (udp == null) return;

        var packet = new byte[HeaderBytes + pcmFrame.Length];
        var seq = Interlocked.Increment(ref _sendSeq);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(0, 4), seq);
        pcmFrame.CopyTo(packet.AsSpan(HeaderBytes));

        var tag = ComputeTag(seq, pcmFrame);
        tag.AsSpan(0, 8).CopyTo(packet.AsSpan(4, 8));

        try { udp.Send(packet, packet.Length, dest); }
        catch { }
    }

    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken cancel)
    {
        uint lastSeq = 0;
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                var res = await udp.ReceiveAsync(cancel);
                var data = res.Buffer;
                if (data.Length <= HeaderBytes) continue;

                var seq = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4));
                var pcm = data.AsSpan(HeaderBytes);

                var expectedTag = ComputeTag(seq, pcm);
                if (!CryptographicOperations.FixedTimeEquals(expectedTag.AsSpan(0, 8), data.AsSpan(4, 8)))
                    continue;

                // Drop severely out-of-order late packets
                if (lastSeq != 0 && unchecked((int)(seq - lastSeq)) < -5)
                    continue;
                lastSeq = seq;

                _lastPacketReceivedTick = Environment.TickCount64;
                _lastAudioFrameTick = _lastPacketReceivedTick;
                _peakLevel = ComputePeak16(pcm);

                var buf = _buffer;
                if (buf != null)
                {
                    // Keep jitter buffer tight (< 90 ms) so audio never lags behind video/games!
                    if (buf.BufferedDuration.TotalMilliseconds > 90)
                        buf.ClearBuffer();
                    buf.AddSamples(data, HeaderBytes, data.Length - HeaderBytes);
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // ignore transient socket error
            }
        }
    }

    private static IWaveProvider Build48kStereo16Pipeline(IWaveProvider source, WaveFormat srcFormat)
    {
        ISampleProvider sampleProvider = source.ToSampleProvider();

        // Downmix/upmix to 2 channels (Stereo)
        if (srcFormat.Channels == 1)
            sampleProvider = new MonoToStereoSampleProvider(sampleProvider);
        else if (srcFormat.Channels > 2)
            sampleProvider = new MultiChannelToStereoProvider(sampleProvider, srcFormat.Channels);

        // Resample to 48 kHz if sound card is 44.1 kHz / 96 kHz / etc.
        if (sampleProvider.WaveFormat.SampleRate != TargetSampleRate)
            sampleProvider = new WdlResamplingSampleProvider(sampleProvider, TargetSampleRate);

        return new SampleToWaveProvider16(sampleProvider);
    }

    /// <summary>
    /// Converts arbitrary WASAPI Loopback format (typically 44.1/48/96 kHz 32-bit IEEE float, 2..8 channels)
    /// to clean 48 kHz 16-bit Stereo PCM.
    /// </summary>
    public static byte[] ConvertTo48kStereo16(byte[] input, int count, WaveFormat srcFormat)
    {
        if (count <= 0) return [];

        using var ms = new MemoryStream(input, 0, count, writable: false);
        using var raw = new RawSourceWaveStream(ms, srcFormat);
        var wave16 = Build48kStereo16Pipeline(raw, srcFormat);
        using var outMs = new MemoryStream();
        var tmp = new byte[4096];
        int read;
        while ((read = wave16.Read(tmp, 0, tmp.Length)) > 0)
            outMs.Write(tmp, 0, read);

        return outMs.ToArray();
    }

    private byte[] ComputeTag(uint seq, ReadOnlySpan<byte> pcm)
    {
        Span<byte> seqBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(seqBuf, seq);
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, _authKey);
        hmac.AppendData(seqBuf);
        hmac.AppendData(pcm);
        return hmac.GetCurrentHash();
    }

    private static float ComputePeak16(ReadOnlySpan<byte> pcm)
    {
        var max = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 8)
        {
            var s = Math.Abs((int)BitConverter.ToInt16(pcm.Slice(i, 2)));
            if (s > max) max = s;
        }
        return Math.Clamp(max / 32768f, 0f, 1f);
    }

    private void ApplyVolume()
    {
        var vp = _volumeProvider;
        if (vp != null)
            vp.Volume = _muted ? 0f : _volume;
    }

    private void StopInternal()
    {
        _mode = AudioShareMode.Off;
        _peakLevel = 0f;
        _cts?.Cancel();
        try { _capture?.StopRecording(); } catch { }
        try { _capture?.Dispose(); } catch { }
        _capture = null;
        try { _player?.Stop(); } catch { }
        try { _player?.Dispose(); } catch { }
        _player = null;
        _buffer = null;
        _volumeProvider = null;
        try { _udp?.Dispose(); } catch { }
        _udp = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        lock (_sync) StopInternal();
    }

    private sealed class MultiChannelToStereoProvider(ISampleProvider source, int channels) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            var frames = count / 2;
            var srcBuf = new float[frames * channels];
            var srcRead = source.Read(srcBuf, 0, srcBuf.Length);
            var framesRead = srcRead / channels;

            for (var f = 0; f < framesRead; f++)
            {
                var baseIdx = f * channels;
                buffer[offset + f * 2] = srcBuf[baseIdx];
                buffer[offset + f * 2 + 1] = srcBuf[baseIdx + 1];
            }
            return framesRead * 2;
        }
    }
}
