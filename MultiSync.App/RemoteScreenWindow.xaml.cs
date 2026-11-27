using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MultiSync.App.Services;
using MultiSync.Core.ClipboardSync;
using MultiSync.Core.Discovery;
using MultiSync.Core.ScreenShare;
using MultiSync.Core.Win32;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfDataFormats = System.Windows.DataFormats;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace MultiSync.App;

public partial class RemoteScreenWindow : Window
{
    private readonly AppCoordinator _coordinator;
    private readonly string _peerName;
    private readonly string _peerIp;
    private readonly int _controlPort;
    private readonly SemaphoreSlim _inputSendLock = new(1, 1);

    private CancellationTokenSource? _streamCts;
    private NetworkStream? _activeStream;
    private ScreenStreamPreset _preset = ScreenStreamPreset.Balanced720p30;
    private int _remoteWidth = 1920;
    private int _remoteHeight = 1080;
    private bool _controlEnabled;
    private bool _isFullscreen;
    private WindowStyle _savedStyle;
    private WindowState _savedState;
    private ResizeMode _savedResize;
    private long _lastMouseSendTick;
    private int _frameRenderPending;

    public RemoteScreenWindow(AppCoordinator coordinator, string peerName, string peerIp, int controlPort)
    {
        InitializeComponent();
        _coordinator = coordinator;
        _peerName = peerName;
        _peerIp = peerIp;
        _controlPort = controlPort;

        Title = $"MultiSync — Tela de {peerName} ({peerIp})";
        HeaderTitleText.Text = $"🖥️ {peerName} ({peerIp})";
        DropOverlayText.Text = $"Solte aqui para enviar arquivos para {peerName}";

        Loaded += (_, _) => StartStreamingLoop();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        StopStreamingLoop();
        _inputSendLock.Dispose();
        base.OnClosing(e);
    }

    private void StartStreamingLoop()
    {
        StopStreamingLoop();
        _streamCts = new CancellationTokenSource();
        var token = _streamCts.Token;
        var preset = _preset;
        _ = Task.Run(() => RunStreamClientAsync(preset, token), token);
    }

    private void StopStreamingLoop()
    {
        _activeStream = null;
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
    }

    private async Task RunStreamClientAsync(ScreenStreamPreset preset, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            TcpClient? tcp = null;
            NetworkStream? stream = null;
            try
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    StreamBadge.Background = new SolidColorBrush(WpfColor.FromRgb(120, 53, 15));
                    StreamStatsText.Foreground = new SolidColorBrush(WpfColor.FromRgb(253, 230, 138));
                    StreamStatsText.Text = "Conectando...";
                });

                var cfg = _coordinator.Store.GetSnapshot();
                (tcp, stream) = await LanDiscoveryAndControlService.ConnectScreenStreamAsync(
                    _peerIp,
                    _controlPort,
                    cfg.DeviceName,
                    cfg.SessionKey,
                    preset,
                    cancel);

                _activeStream = stream;

                var fpsCount = 0;
                var fpsTick = Environment.TickCount64;

                while (!cancel.IsCancellationRequested)
                {
                    var frame = await ScreenStreamEngine.ReadNextFrameAsync(stream, cancel);
                    if (frame == null) continue;

                    _remoteWidth = Math.Max(1, frame.SourceWidth);
                    _remoteHeight = Math.Max(1, frame.SourceHeight);
                    fpsCount++;

                    // Drop frame if UI is still painting the previous one so latency stays near zero
                    if (Interlocked.CompareExchange(ref _frameRenderPending, 1, 0) == 0)
                    {
                        var bmp = DecodeFrozenBitmap(frame.JpegBytes);
                        if (bmp != null)
                        {
                            _ = Dispatcher.BeginInvoke(() =>
                            {
                                ScreenImage.Source = bmp;
                                if (ConnectingOverlayText.Visibility == Visibility.Visible)
                                    ConnectingOverlayText.Visibility = Visibility.Collapsed;
                                Volatile.Write(ref _frameRenderPending, 0);
                            });
                        }
                        else
                        {
                            Volatile.Write(ref _frameRenderPending, 0);
                        }
                    }

                    var now = Environment.TickCount64;
                    if (now - fpsTick >= 1000)
                    {
                        var measuredFps = fpsCount;
                        fpsCount = 0;
                        fpsTick = now;
                        var w = _remoteWidth;
                        var h = _remoteHeight;
                        _ = Dispatcher.BeginInvoke(() =>
                        {
                            StreamBadge.Background = new SolidColorBrush(WpfColor.FromRgb(6, 78, 59));
                            StreamStatsText.Foreground = new SolidColorBrush(WpfColor.FromRgb(110, 231, 183));
                            StreamStatsText.Text = $"Ao vivo • {w}×{h} • {measuredFps} FPS";
                        });
                    }
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    StreamBadge.Background = new SolidColorBrush(WpfColor.FromRgb(127, 29, 29));
                    StreamStatsText.Foreground = new SolidColorBrush(WpfColor.FromRgb(252, 165, 165));
                    StreamStatsText.Text = "Reconectando...";
                    ConnectingOverlayText.Text = $"Aguardando {_peerName} ({ex.Message})...";
                    ConnectingOverlayText.Visibility = Visibility.Visible;
                });
            }
            finally
            {
                _activeStream = null;
                try { stream?.Dispose(); } catch { }
                try { tcp?.Dispose(); } catch { }
            }

            try { await Task.Delay(1500, cancel); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static BitmapImage? DecodeFrozenBitmap(byte[] jpegBytes)
    {
        try
        {
            using var ms = new MemoryStream(jpegBytes, writable: false);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    // ==================== DIRECT CONTROL INSIDE VIEWER ====================

    private void SendViewerInputAsync(byte[] packet)
    {
        var stream = _activeStream;
        if (stream == null) return;

        _ = Task.Run(async () =>
        {
            if (!await _inputSendLock.WaitAsync(50)) return;
            try
            {
                if (_activeStream == stream)
                    await stream.WriteAsync(packet);
            }
            catch { }
            finally
            {
                _inputSendLock.Release();
            }
        });
    }

    private (int X, int Y)? MapImagePointToRemote(WpfMouseEventArgs e)
    {
        var w = ScreenImage.ActualWidth;
        var h = ScreenImage.ActualHeight;
        if (w <= 1 || h <= 1) return null;

        var pt = e.GetPosition(ScreenImage);
        var rx = Math.Clamp((int)Math.Round(pt.X / w * (_remoteWidth - 1)), 0, _remoteWidth - 1);
        var ry = Math.Clamp((int)Math.Round(pt.Y / h * (_remoteHeight - 1)), 0, _remoteHeight - 1);
        return (rx, ry);
    }

    private void OnScreenMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (!_controlEnabled) return;
        var now = Environment.TickCount64;
        if (now - _lastMouseSendTick < 8) return; // <= 125 Hz inside viewer
        _lastMouseSendTick = now;

        if (MapImagePointToRemote(e) is var (rx, ry))
            SendViewerInputAsync(ScreenStreamEngine.BuildMouseMovePacket(rx, ry));
    }

    private void OnScreenMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_controlEnabled) return;
        if (MapImagePointToRemote(e) is var (rx, ry))
            SendViewerInputAsync(ScreenStreamEngine.BuildMouseMovePacket(rx, ry));

        var btn = MapMouseButton(e.ChangedButton);
        if (btn != null)
            SendViewerInputAsync(ScreenStreamEngine.BuildMouseButtonPacket(btn.Value, isDown: true));
    }

    private void OnScreenMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_controlEnabled) return;
        var btn = MapMouseButton(e.ChangedButton);
        if (btn != null)
            SendViewerInputAsync(ScreenStreamEngine.BuildMouseButtonPacket(btn.Value, isDown: false));
    }

    private void OnScreenMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_controlEnabled) return;
        SendViewerInputAsync(ScreenStreamEngine.BuildMouseWheelPacket(e.Delta));
    }

    private void OnWindowPreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _isFullscreen)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }
        if (!_controlEnabled) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var vk = (ushort)KeyInterop.VirtualKeyFromKey(key);
        if (vk != 0)
        {
            SendViewerInputAsync(ScreenStreamEngine.BuildKeyPacket(vk, isDown: true));
            e.Handled = true;
        }
    }

    private void OnWindowPreviewKeyUp(object sender, WpfKeyEventArgs e)
    {
        if (!_controlEnabled || e.Key == Key.F11) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var vk = (ushort)KeyInterop.VirtualKeyFromKey(key);
        if (vk != 0)
        {
            SendViewerInputAsync(ScreenStreamEngine.BuildKeyPacket(vk, isDown: false));
            e.Handled = true;
        }
    }

    private static MouseBtn? MapMouseButton(MouseButton button) => button switch
    {
        MouseButton.Left => MouseBtn.Left,
        MouseButton.Right => MouseBtn.Right,
        MouseButton.Middle => MouseBtn.Middle,
        MouseButton.XButton1 => MouseBtn.XButton1,
        MouseButton.XButton2 => MouseBtn.XButton2,
        _ => null,
    };

    // ==================== DRAG & DROP FILES ONTO REMOTE SCREEN ====================

    private void OnWindowDragEnter(object sender, WpfDragEventArgs e)
    {
        if (e.Data.GetDataPresent(WpfDataFormats.FileDrop))
        {
            e.Effects = WpfDragDropEffects.Copy;
            DropOverlay.Visibility = Visibility.Visible;
            e.Handled = true;
        }
    }

    private void OnWindowDragOver(object sender, WpfDragEventArgs e)
    {
        if (e.Data.GetDataPresent(WpfDataFormats.FileDrop))
        {
            e.Effects = WpfDragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnWindowDragLeave(object sender, WpfDragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void OnWindowDrop(object sender, WpfDragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!e.Data.GetDataPresent(WpfDataFormats.FileDrop)) return;
        if (e.Data.GetData(WpfDataFormats.FileDrop) is not string[] dropped || dropped.Length == 0)
            return;

        var built = ClipboardSyncManager.BuildFileManifest(dropped);
        if (built == null)
        {
            FooterInfoText.Text = "Arquivos excedem o limite de 2 GB.";
            return;
        }

        try
        {
            var cfg = _coordinator.Store.GetSnapshot();
            var count = built.Value.Manifest.Entries.Count(x => !x.IsDirectory);
            FooterInfoText.Text = $"📤 Enviando {count} arquivo(s) para {_peerName}...";

            await LanDiscoveryAndControlService.SendFilesToPeerAsync(
                targetIp: _peerIp,
                controlPort: _controlPort,
                senderName: cfg.DeviceName,
                sessionKey: cfg.SessionKey,
                manifest: built.Value.Manifest,
                fullPaths: built.Value.FullPathByRelative,
                onProgress: msg => Dispatcher.BeginInvoke(() => FooterInfoText.Text = msg));

            FooterInfoText.Text = $"✅ {count} arquivo(s) enviado(s) para {_peerName}! Aperte Ctrl+V lá para colar.";
        }
        catch (Exception ex)
        {
            FooterInfoText.Text = $"Erro ao enviar arquivos: {ex.Message}";
        }
    }

    // ==================== TOOLBAR ACTIONS ====================

    private void OnQualitySelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _preset = QualityCombo.SelectedIndex switch
        {
            1 => ScreenStreamPreset.Sharp1080p30,
            2 => ScreenStreamPreset.Smooth720p60,
            _ => ScreenStreamPreset.Balanced720p30,
        };
        StartStreamingLoop();
    }

    private void OnControlToggleClick(object sender, RoutedEventArgs e)
    {
        _controlEnabled = ControlToggle.IsChecked == true;
        ScreenImage.Cursor = _controlEnabled ? WpfCursors.Cross : WpfCursors.Arrow;
        FooterInfoText.Text = _controlEnabled
            ? $"🖱️ Controle direto ativado: cliques e teclas nesta janela controlam {_peerName}."
            : "Modo visualização: arraste arquivos para cá ou ative 'Controlar aqui' para interagir.";
    }

    private void OnTopmostToggleClick(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostToggle.IsChecked == true;
    }

    private void OnFullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _savedStyle = WindowStyle;
            _savedState = WindowState;
            _savedResize = ResizeMode;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            TopToolbar.Visibility = Visibility.Collapsed;
            BottomBar.Visibility = Visibility.Collapsed;
            _isFullscreen = true;
        }
        else
        {
            WindowStyle = _savedStyle;
            ResizeMode = _savedResize;
            WindowState = _savedState;
            TopToolbar.Visibility = Visibility.Visible;
            BottomBar.Visibility = Visibility.Visible;
            _isFullscreen = false;
        }
    }
}
