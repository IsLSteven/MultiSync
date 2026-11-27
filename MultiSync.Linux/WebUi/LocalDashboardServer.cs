using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using MultiSync.Linux.Audio;
using MultiSync.Linux.Network;
using MultiSync.Linux.Protocol;

namespace MultiSync.Linux.WebUi;

/// <summary>
/// Zero-dependency local-only (127.0.0.1:5055) dark-mode visual dashboard for Linux (Debian, Mint, Ubuntu, Arch).
/// Supports LAN pairing, Master/Slave layout sync, Shared Audio toggle, and live MJPEG peer screen viewing (up to 1080p 60 FPS).
/// </summary>
public sealed class LocalDashboardServer(LinuxMultiSyncCoordinator coordinator, int port = 5055) : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;

    public string Url => $"http://127.0.0.1:{port}/";

    public void Start()
    {
        _listener.Prefixes.Add(Url);
        _listener.Start();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener.GetContextAsync().WaitAsync(cancel);
                _ = Task.Run(() => HandleRequestAsync(ctx, cancel), cancel);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken cancel)
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        try
        {
            var path = req.Url?.AbsolutePath ?? "/";
            if (path == "/" && req.HttpMethod == "GET")
            {
                await WriteTextAsync(resp, DashboardHtml, "text/html; charset=utf-8");
                return;
            }

            if (path == "/api/status" && req.HttpMethod == "GET")
            {
                var cfg = coordinator.Store.GetSnapshot();
                var payload = new
                {
                    version = "v0.6.0",
                    distro = SystemInfo.DetectDistroSummary(),
                    displayServer = SystemInfo.IsWayland ? "Wayland" : "X11",
                    inputBackend = coordinator.Injector.ActiveBackend,
                    deviceName = cfg.DeviceName,
                    ip = LanAddressHelper.GetPreferredIPv4() ?? "Sem IP",
                    role = cfg.Role.ToString(),
                    pin = cfg.PairingPin,
                    slaveConnected = coordinator.SlaveConnectedToMaster,
                    connectedSlaves = coordinator.ConnectedSlaves,
                    pingMs = coordinator.LatestPingMs,
                    audioMode = coordinator.Audio.Mode.ToString(),
                    statusMessage = coordinator.LastStatusMessage,
                    layoutNodes = cfg.LayoutNodes,
                    peers = coordinator.GetDiscoveredDevices().Select(d => new
                    {
                        d.Name,
                        ip = d.PrimaryAddress,
                        d.Width,
                        d.Height,
                        role = d.Role.ToString(),
                        d.ControlPort,
                        isPaired = cfg.PairedDevices.ContainsKey(d.Name),
                    }),
                };
                await WriteJsonAsync(resp, payload);
                return;
            }

            if (path == "/api/pin" && req.HttpMethod == "POST")
            {
                var pin = coordinator.Store.RegeneratePin();
                await WriteJsonAsync(resp, new { pin });
                return;
            }

            if (path == "/api/pair" && req.HttpMethod == "POST")
            {
                using var sr = new StreamReader(req.InputStream, Encoding.UTF8);
                var body = JsonSerializer.Deserialize<PairBody>(await sr.ReadToEndAsync(cancel), ConfigStore.JsonOptions)!;
                var res = await coordinator.PairWithDeviceAsync(body.Ip, body.Port > 0 ? body.Port : 5052, body.Pin, cancel);
                await WriteJsonAsync(resp, new { ok = true, device = res.DeviceName });
                return;
            }

            if (path == "/api/swap-master" && req.HttpMethod == "POST")
            {
                var cfg = coordinator.Store.GetSnapshot();
                if (cfg.LayoutNodes.Count >= 2)
                {
                    var idx = cfg.LayoutNodes.FindIndex(n => n.IsMaster);
                    var next = (idx + 1) % cfg.LayoutNodes.Count;
                    for (var i = 0; i < cfg.LayoutNodes.Count; i++) cfg.LayoutNodes[i].IsMaster = (i == next);
                    var synced = await coordinator.ApplyLayoutAsync(cfg.LayoutNodes, cancel);
                    await WriteJsonAsync(resp, new { ok = true, synced });
                    return;
                }
                await WriteJsonAsync(resp, new { ok = false, error = "Pareie um segundo computador primeiro." });
                return;
            }

            if (path == "/api/audio/toggle" && req.HttpMethod == "POST")
            {
                if (coordinator.Audio.Mode == AudioShareMode.Off)
                {
                    var started = await coordinator.EnableReceiveAudioHereAsync(cancel);
                    await WriteJsonAsync(resp, new { ok = started > 0, mode = coordinator.Audio.Mode.ToString() });
                }
                else
                {
                    await coordinator.DisableSharedAudioAsync(cancel);
                    await WriteJsonAsync(resp, new { ok = true, mode = "Off" });
                }
                return;
            }

            if (path == "/api/screen" && req.HttpMethod == "GET")
            {
                var targetIp = req.QueryString["ip"] ?? "";
                var preset = req.QueryString["preset"] ?? "720p30";
                var (maxW, fps, quality) = preset switch
                {
                    "1080p60" => (1920, 60, 72),
                    "1080p30" => (1920, 30, 76),
                    "720p60" => (1280, 60, 64),
                    _ => (1280, 30, 70),
                };

                resp.ContentType = "multipart/x-mixed-replace; boundary=frame";
                resp.SendChunked = true;
                var (tcp, peerStream) = await coordinator.ConnectPeerScreenStreamAsync(targetIp, maxW, fps, quality, cancel);
                using (tcp)
                await using (peerStream)
                {
                    var hdr = new byte[5];
                    while (!cancel.IsCancellationRequested)
                    {
                        await peerStream.ReadExactlyAsync(hdr, cancel);
                        var len = BinaryPrimitives.ReadInt32BigEndian(hdr.AsSpan(1, 4));
                        if (len < 8 || len > 16 * 1024 * 1024) break;
                        var body = new byte[len];
                        await peerStream.ReadExactlyAsync(body, cancel);
                        if (hdr[0] != 1) continue;

                        var jpegLen = len - 8;
                        var partHdr = Encoding.ASCII.GetBytes($"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {jpegLen}\r\n\r\n");
                        await resp.OutputStream.WriteAsync(partHdr, cancel);
                        await resp.OutputStream.WriteAsync(body.AsMemory(8, jpegLen), cancel);
                        await resp.OutputStream.WriteAsync("\r\n"u8.ToArray(), cancel);
                        await resp.OutputStream.FlushAsync(cancel);
                    }
                }
                return;
            }

            resp.StatusCode = 404;
            resp.Close();
        }
        catch (Exception ex)
        {
            try
            {
                resp.StatusCode = 500;
                await WriteJsonAsync(resp, new { ok = false, error = ex.Message });
            }
            catch { }
        }
    }

    private sealed record PairBody(string Ip, int Port, string Pin);

    private static async Task WriteTextAsync(HttpListenerResponse resp, string text, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        resp.ContentType = contentType;
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes);
        resp.Close();
    }

    private static Task WriteJsonAsync(HttpListenerResponse resp, object obj) =>
        WriteTextAsync(resp, JsonSerializer.Serialize(obj, ConfigStore.JsonOptions), "application/json; charset=utf-8");

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
        try { _loopTask?.Wait(TimeSpan.FromMilliseconds(500)); } catch { }
        _cts.Dispose();
    }

    private const string DashboardHtml = """
<!DOCTYPE html>
<html lang="pt-BR">
<head>
<meta charset="utf-8"/>
<title>MultiSync Linux — Painel Multi-PC</title>
<style>
  body { margin:0; font-family: system-ui, -apple-system, Segoe UI, sans-serif; background:#0d1017; color:#f3f4f6; }
  .wrap { max-width: 1080px; margin: 20px auto; padding: 0 16px; }
  .card { background:#151a24; border:1px solid #252e42; border-radius:10px; padding:16px; margin-bottom:14px; }
  .row { display:flex; gap:14px; flex-wrap:wrap; align-items:center; justify-content:space-between; }
  .badge { background:#064e3b; color:#6ee7b7; padding:3px 10px; border-radius:99px; font-size:12px; font-weight:600; }
  .pin { font-family:monospace; font-size:20px; font-weight:700; color:#38bdf8; letter-spacing:2px; }
  button { background:#2563eb; color:#fff; border:1px solid #3b82f6; padding:8px 14px; border-radius:6px; font-weight:600; cursor:pointer; }
  button.sec { background:#242c3d; border-color:#344059; }
  input, select { background:#0d1017; color:#f3f4f6; border:1px solid #2e384d; padding:7px 10px; border-radius:6px; }
  .grid { display:grid; grid-template-columns: 1.5fr 1fr; gap:14px; }
  .peer { background:#101520; border:1px solid #242e42; border-radius:8px; padding:12px; margin-bottom:10px; }
  #viewerModal { display:none; position:fixed; inset:0; background:rgba(5,7,11,.94); z-index:99; padding:18px; flex-direction:column; }
  #viewerImg { max-width:100%; max-height:calc(100vh - 90px); object-fit:contain; margin:auto; border:1px solid #252e42; border-radius:8px; }
  .muted { color:#9ca3af; font-size:12.5px; }
</style>
</head>
<body>
<div class="wrap">
  <div class="card row">
    <div>
      <strong style="font-size:20px">⇄ MultiSync Linux</strong>
      <span id="roleBadge" class="badge">Carregando...</span>
      <div id="sysInfo" class="muted" style="margin-top:4px"></div>
    </div>
    <div class="row">
      <span>PIN deste PC: <span id="pinCode" class="pin">----</span></span>
      <button class="sec" onclick="regenPin()">Novo PIN</button>
    </div>
  </div>

  <div class="grid">
    <div class="card">
      <div class="row" style="margin-bottom:12px">
        <strong>Layout & Controle Master / Slave</strong>
        <button class="sec" onclick="swapMaster()">⇄ Inverter Master / Slave</button>
      </div>
      <div id="nodesBox" class="row" style="justify-content:flex-start"></div>
      <div id="statusMsg" class="muted" style="margin-top:14px"></div>
    </div>

    <div class="card">
      <strong>Dispositivos na Rede (LAN)</strong>
      <div id="peersBox" style="margin-top:10px"></div>
      <div class="row" style="margin-top:10px">
        <input id="mIp" placeholder="IP (ex: 192.168.2.5)" style="width:140px"/>
        <input id="mPin" placeholder="PIN" maxlength="4" style="width:62px"/>
        <button onclick="pairManual()">Parear</button>
      </div>
    </div>
  </div>

  <div class="card row">
    <div>
      <strong>🎧 Áudio Compartilhado (PipeWire / PulseAudio ↔ Windows)</strong>
      <div id="audioInfo" class="muted">Desligado</div>
    </div>
    <button id="audioBtn" onclick="toggleAudio()">🎧 Ouvir outro PC neste fone</button>
  </div>

  <div class="row muted" style="padding:4px 4px">
    <span>MultiSync Cross-Platform • Debian / Mint / Ubuntu / Arch ↔ Windows</span>
    <span id="verText">v0.6.0</span>
  </div>
</div>

<div id="viewerModal">
  <div class="row" style="margin-bottom:10px">
    <strong id="viewerTitle">🖥️ Tela Remota</strong>
    <div class="row">
      <select id="presetSel" onchange="reloadViewer()">
        <option value="720p30">720p • 30 FPS (Padrão)</option>
        <option value="1080p30">1080p • 30 FPS (Nítido)</option>
        <option value="720p60">720p • 60 FPS (Suave)</option>
        <option value="1080p60">1080p • 60 FPS (Ultra)</option>
      </select>
      <button class="sec" onclick="closeViewer()">✕ Fechar</button>
    </div>
  </div>
  <img id="viewerImg" alt="Tela Remota"/>
</div>

<script>
let activePeerIp = '';
async function refresh() {
  const s = await (await fetch('/api/status')).json();
  document.getElementById('roleBadge').textContent = s.role + (s.slaveConnected ? ' • Conectado' : (s.connectedSlaves.length ? ' • ' + s.connectedSlaves.length + ' conectado(s)' : ''));
  document.getElementById('sysInfo').textContent = `${s.deviceName} (${s.ip}) • ${s.distro} • ${s.displayServer} • Input: ${s.inputBackend}`;
  document.getElementById('pinCode').textContent = s.pin;
  document.getElementById('statusMsg').textContent = s.statusMessage;
  document.getElementById('audioInfo').textContent = 'Estado: ' + s.audioMode;
  document.getElementById('audioBtn').textContent = s.audioMode === 'Off' ? '🎧 Ouvir outro PC neste fone' : '⏹ Desligar Áudio';
  document.getElementById('verText').textContent = s.version;

  document.getElementById('nodesBox').innerHTML = (s.layoutNodes || []).map(n =>
    `<div class="peer" style="min-width:190px;border-color:${n.isMaster ? '#3b82f6' : '#374151'}">
      <div style="font-size:11px;font-weight:700;color:${n.isMaster ? '#60a5fa' : '#9ca3af'}">${n.isMaster ? '★ MASTER' : 'SLAVE'}</div>
      <div style="font-size:15px;font-weight:700;margin:4px 0">${n.name}</div>
      <div class="muted">${n.ipAddress || ''} • ${n.width}×${n.height}</div>
    </div>`
  ).join('');

  document.getElementById('peersBox').innerHTML = (s.peers || []).length === 0
    ? '<div class="muted">Nenhum outro PC detectado ainda na rede local.</div>'
    : s.peers.map(p => `<div class="peer">
        <div class="row"><strong>${p.name}</strong><span class="badge">${p.isPaired ? 'Pareado' : 'Na Rede'}</span></div>
        <div class="muted" style="margin:4px 0 8px">${p.ip} • ${p.width}×${p.height} • ${p.role}</div>
        ${p.isPaired
          ? `<button onclick="openViewer('${p.name}','${p.ip}')">🖥️ Ver Tela ao Vivo</button>`
          : `<div class="row"><input id="pin_${p.name}" placeholder="PIN" maxlength="4" style="width:70px"/><button onclick="pairPeer('${p.ip}',${p.controlPort},'pin_${p.name}')">Parear PIN</button></div>`}
      </div>`).join('');
}
async function regenPin() { await fetch('/api/pin', {method:'POST'}); refresh(); }
async function pairPeer(ip, port, inputId) {
  const pin = document.getElementById(inputId).value;
  await fetch('/api/pair', {method:'POST', body: JSON.stringify({ip, port, pin})});
  refresh();
}
async function pairManual() {
  await fetch('/api/pair', {method:'POST', body: JSON.stringify({ip: mIp.value, port: 5052, pin: mPin.value})});
  refresh();
}
async function swapMaster() { await fetch('/api/swap-master', {method:'POST'}); refresh(); }
async function toggleAudio() { await fetch('/api/audio/toggle', {method:'POST'}); refresh(); }
function openViewer(name, ip) {
  activePeerIp = ip;
  document.getElementById('viewerTitle').textContent = '🖥️ Tela de ' + name + ' (' + ip + ')';
  document.getElementById('viewerModal').style.display = 'flex';
  reloadViewer();
}
function reloadViewer() {
  if (!activePeerIp) return;
  const preset = document.getElementById('presetSel').value;
  document.getElementById('viewerImg').src = `/api/screen?ip=${encodeURIComponent(activePeerIp)}&preset=${preset}&t=${Date.now()}`;
}
function closeViewer() {
  activePeerIp = '';
  document.getElementById('viewerImg').src = '';
  document.getElementById('viewerModal').style.display = 'none';
}
refresh();
setInterval(refresh, 2500);
</script>
</body>
</html>
""";
}
