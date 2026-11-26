using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MultiSync.App.Services;
using MultiSync.Core.Audio;
using MultiSync.Core.Models;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfPanel = System.Windows.Controls.Panel;
using WpfPoint = System.Windows.Point;

namespace MultiSync.App;

public partial class MainWindow : Window
{
    private const double CardWidth = 212;
    private const double CardHeight = 134;
    private const double CellStepX = 250;
    private const double CellStepY = 170;

    private readonly AppCoordinator _coordinator;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _audioMeterTimer;
    private readonly List<DeviceNode> _nodes = [];
    private readonly Dictionary<string, string> _pinInputs = new(StringComparer.OrdinalIgnoreCase);

    private DeviceNode? _draggingNode;
    private Border? _draggingCard;
    private WpfPoint _dragOffset;
    private (int GridX, int GridY)? _snapTarget;
    private Border? _snapGhost;

    public bool AllowClose { get; set; }

    public MainWindow(AppCoordinator coordinator)
    {
        InitializeComponent();
        _coordinator = coordinator;
        _coordinator.StateUpdated += () => Dispatcher.BeginInvoke(RefreshStatusAndLan);
        _coordinator.Engine.TransferActivity += msg => Dispatcher.BeginInvoke(() => SetStatus(msg));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _timer.Tick += (_, _) => RefreshStatusAndLan();

        _audioMeterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _audioMeterTimer.Tick += (_, _) =>
        {
            if (_coordinator.Audio.Mode == AudioShareMode.Off)
            {
                AudioPeakBar.Value = 0;
                _audioMeterTimer.Stop();
                return;
            }
            AudioPeakBar.Value = Math.Clamp(_coordinator.Audio.PeakLevel * 100.0, 0, 100);
        };

        Loaded += async (_, _) =>
        {
            LoadNodesFromStore();
            AutoStartCheck.IsChecked = _coordinator.IsAutoStartEnabled();
            RefreshStatusAndLan();
            await _coordinator.Lan.BroadcastProbeAsync();
            _timer.Start();
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _timer.Stop();
        _audioMeterTimer.Stop();
        base.OnClosing(e);
    }

    public async Task QuickSwapMasterSlaveFromTrayAsync()
    {
        await Dispatcher.InvokeAsync(async () =>
        {
            if (_nodes.Count < 2) return;
            CycleMasterNode();
            await ApplyLayoutInternalAsync();
        });
    }

    private void LoadNodesFromStore()
    {
        _nodes.Clear();
        var cfg = _coordinator.Store.GetSnapshot();
        FullscreenGuardCheck.IsChecked = cfg.BlockFullscreenEdge;

        foreach (var n in cfg.LayoutNodes)
        {
            _nodes.Add(new DeviceNode
            {
                Name = n.Name,
                IpAddress = n.IpAddress,
                Width = n.Width,
                Height = n.Height,
                GridX = n.GridX,
                GridY = n.GridY,
                IsMaster = n.IsMaster,
                IsLocal = n.IsLocal || string.Equals(n.Name, cfg.DeviceName, StringComparison.OrdinalIgnoreCase),
            });
        }

        EnsureExactlyOneMaster();
        RenderCanvas();
    }

    private void RefreshStatusAndLan()
    {
        var cfg = _coordinator.Store.GetSnapshot();
        PinCodeText.Text = cfg.PairingPin;

        var myIp = LanAddressHelper.GetPreferredIPv4() ?? "Sem IP LAN";
        LocalMachineInfoText.Text = $"Este PC: {cfg.DeviceName} ({myIp})  •  Porta KVM: {cfg.KvmPort}";

        var eng = _coordinator.Engine;
        var lockSuffix = eng.IsLockedToScreen ? " • Tela Travada" : "";
        if (cfg.Role == DeviceRole.Master)
        {
            var count = eng.ConnectedPeers.Count;
            EngineBadge.Background = new SolidColorBrush(WpfColor.FromRgb(6, 78, 59));
            EngineStatusText.Foreground = new SolidColorBrush(WpfColor.FromRgb(110, 231, 183));
            EngineStatusText.Text = $"Master • {count} conectado(s){lockSuffix}";
            LatencyText.Text = count > 0 && eng.LatestPingMs > 0 ? $"Ping LAN: {eng.LatestPingMs:0.0} ms" : "";
        }
        else
        {
            var ok = eng.SlaveConnectedToMaster;
            EngineBadge.Background = new SolidColorBrush(ok ? WpfColor.FromRgb(6, 78, 59) : WpfColor.FromRgb(120, 53, 15));
            EngineStatusText.Foreground = new SolidColorBrush(ok ? WpfColor.FromRgb(110, 231, 183) : WpfColor.FromRgb(253, 230, 138));
            EngineStatusText.Text = ok ? $"Slave • Conectado ao Master{lockSuffix}" : $"Slave • Aguardando Master ({cfg.MasterIpAddress})";
            LatencyText.Text = "";
        }

        // Sync canvas if a remote peer pushed a new Master/Slave layout to this machine
        if (_draggingNode == null && UnsavedHintText.Visibility != Visibility.Visible && cfg.LayoutNodes.Count > 0)
        {
            var localMasterInStore = cfg.LayoutNodes.FirstOrDefault(n => n.IsMaster)?.Name;
            var localMasterInUi = _nodes.FirstOrDefault(n => n.IsMaster)?.Name;
            if (!string.Equals(localMasterInStore, localMasterInUi, StringComparison.OrdinalIgnoreCase) ||
                cfg.LayoutNodes.Count != _nodes.Count)
            {
                LoadNodesFromStore();
            }
        }

        var peers = _coordinator.GetLanPeersSnapshot();
        var updatedNodeIps = false;
        foreach (var p in peers)
        {
            var match = _nodes.FirstOrDefault(n => string.Equals(n.Name, p.Name, StringComparison.OrdinalIgnoreCase));
            if (match != null && !match.IsLocal && !string.IsNullOrWhiteSpace(p.IpAddress) && match.IpAddress != p.IpAddress)
            {
                match.IpAddress = p.IpAddress;
                updatedNodeIps = true;
            }
        }
        if (updatedNodeIps && _draggingNode == null)
            RenderCanvas();

        RenderLanPeersList(peers);
        RefreshAudioUi();
    }

    private void RefreshAudioUi()
    {
        var audio = _coordinator.Audio;
        AudioMuteButton.Content = audio.IsMuted ? "🔇" : "🔊";

        if (audio.Mode == AudioShareMode.ReceiveHere)
        {
            AudioBadge.Background = new SolidColorBrush(WpfColor.FromRgb(6, 78, 59));
            AudioBadgeText.Foreground = new SolidColorBrush(WpfColor.FromRgb(110, 231, 183));
            AudioBadgeText.Text = audio.IsReceivingActive ? "Recebendo ao vivo" : "Aguardando áudio";
            AudioStatusText.Text = audio.IsMuted
                ? "Áudio remoto silenciado neste fone (48 kHz Estéreo via UDP 5053)."
                : "Reproduzindo áudio do outro computador no seu fone em tempo real (48 kHz Estéreo).";
            AudioToggleButton.Content = "⏹ Desligar Áudio";
            if (!_audioMeterTimer.IsEnabled) _audioMeterTimer.Start();
        }
        else if (audio.Mode == AudioShareMode.SendToPeer)
        {
            AudioBadge.Background = new SolidColorBrush(WpfColor.FromRgb(30, 58, 138));
            AudioBadgeText.Foreground = new SolidColorBrush(WpfColor.FromRgb(147, 197, 253));
            AudioBadgeText.Text = "Transmitindo";
            AudioStatusText.Text = $"Enviando o áudio deste PC para {audio.RemoteEndpointIp ?? "o outro PC"} em tempo real (WASAPI Loopback).";
            AudioToggleButton.Content = "⏹ Parar Transmissão";
            if (!_audioMeterTimer.IsEnabled) _audioMeterTimer.Start();
        }
        else
        {
            AudioBadge.Background = new SolidColorBrush(WpfColor.FromRgb(31, 41, 55));
            AudioBadgeText.Foreground = new SolidColorBrush(WpfColor.FromRgb(156, 163, 175));
            AudioBadgeText.Text = "Desligado";
            AudioStatusText.Text = "Clique no botão ao lado no PC onde seu fone está conectado para ouvir o som do outro PC em tempo real.";
            AudioToggleButton.Content = "🎧 Ouvir outro PC neste fone";
            AudioPeakBar.Value = 0;
            if (_audioMeterTimer.IsEnabled) _audioMeterTimer.Stop();
        }
    }

    private void RenderLanPeersList(List<LanPeerItem> peers)
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox focusedTb && LanDevicesStack.IsAncestorOf(focusedTb))
            return;

        LanDevicesStack.Children.Clear();
        if (peers.Count == 0)
        {
            LanDevicesStack.Children.Add(new TextBlock
            {
                Text = "Nenhum outro computador detectado ainda.\n\nAbra o MultiSync no segundo PC conectado na mesma rede.",
                Foreground = new SolidColorBrush(WpfColor.FromRgb(156, 163, 175)),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(6, 10, 6, 10),
            });
            return;
        }

        foreach (var peer in peers)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(WpfColor.FromRgb(23, 29, 43)),
                BorderBrush = new SolidColorBrush(peer.IsConnectedLive
                    ? WpfColor.FromRgb(16, 185, 129)
                    : WpfColor.FromRgb(42, 51, 71)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 8),
            };

            var stack = new StackPanel();

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameText = new TextBlock
            {
                Text = peer.Name,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = WpfBrushes.White,
            };
            Grid.SetColumn(nameText, 0);
            header.Children.Add(nameText);

            var badgeLabel = peer.IsConnectedLive ? "Online" : (peer.IsPaired ? "Pareado" : "Na Rede");
            var badgeColor = peer.IsConnectedLive
                ? WpfColor.FromRgb(6, 95, 70)
                : (peer.IsPaired ? WpfColor.FromRgb(30, 58, 138) : WpfColor.FromRgb(55, 65, 81));
            var badge = new Border
            {
                Background = new SolidColorBrush(badgeColor),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 2, 7, 2),
                Child = new TextBlock
                {
                    Text = badgeLabel,
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = WpfBrushes.White,
                },
            };
            Grid.SetColumn(badge, 1);
            header.Children.Add(badge);
            stack.Children.Add(header);

            stack.Children.Add(new TextBlock
            {
                Text = $"{peer.IpAddress}  •  {peer.Width}×{peer.Height}  •  {peer.Role}",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(WpfColor.FromRgb(156, 163, 175)),
                Margin = new Thickness(0, 3, 0, 8),
            });

            if (!peer.IsPaired && !string.IsNullOrWhiteSpace(peer.IpAddress))
            {
                var pairGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                pairGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                pairGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
                pairGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var pinBox = new System.Windows.Controls.TextBox
                {
                    Style = (Style)FindResource("ModernTextBox"),
                    MaxLength = 4,
                    Text = _pinInputs.GetValueOrDefault(peer.Name, ""),
                    ToolTip = $"PIN de 4 dígitos exibido em {peer.Name}",
                };
                pinBox.TextChanged += (_, _) => _pinInputs[peer.Name] = pinBox.Text;
                Grid.SetColumn(pinBox, 0);
                pairGrid.Children.Add(pinBox);

                var pairBtn = new System.Windows.Controls.Button
                {
                    Content = "Parear PIN",
                    Style = (Style)FindResource("PrimaryButton"),
                    Padding = new Thickness(10, 4, 10, 4),
                    FontSize = 11.5,
                };
                pairBtn.Click += async (_, _) => await PairDeviceAsync(peer.IpAddress, peer.ControlPort, pinBox.Text);
                Grid.SetColumn(pairBtn, 2);
                pairGrid.Children.Add(pairBtn);

                stack.Children.Add(pairGrid);
            }

            if (!_nodes.Any(n => string.Equals(n.Name, peer.Name, StringComparison.OrdinalIgnoreCase)))
            {
                var addBtn = new System.Windows.Controls.Button
                {
                    Content = "+ Colocar no Layout Visual",
                    Style = (Style)FindResource("ModernButton"),
                    Padding = new Thickness(8, 5, 8, 5),
                    FontSize = 11.5,
                };
                addBtn.Click += (_, _) =>
                {
                    var maxX = _nodes.Select(n => n.GridX).DefaultIfEmpty(0).Max();
                    _nodes.Add(new DeviceNode
                    {
                        Name = peer.Name,
                        IpAddress = peer.IpAddress,
                        Width = peer.Width,
                        Height = peer.Height,
                        GridX = maxX + 1,
                        GridY = 0,
                        IsMaster = false,
                        IsLocal = false,
                    });
                    UnsavedHintText.Visibility = Visibility.Visible;
                    RenderCanvas();
                };
                stack.Children.Add(addBtn);
            }

            card.Child = stack;
            LanDevicesStack.Children.Add(card);
        }
    }

    // ==================== VISUAL DRAG & DROP CANVAS ====================

    private void RenderCanvas()
    {
        if (MonitorCanvas.ActualWidth <= 10 || MonitorCanvas.ActualHeight <= 10)
            return;

        MonitorCanvas.Children.Clear();
        DrawCanvasGrid();

        if (_nodes.Count == 0) return;

        var (ox, oy) = GetCanvasOrigin();

        for (var i = 0; i < _nodes.Count; i++)
        {
            for (var j = i + 1; j < _nodes.Count; j++)
            {
                var a = _nodes[i];
                var b = _nodes[j];
                if (Math.Abs(a.GridX - b.GridX) + Math.Abs(a.GridY - b.GridY) == 1)
                {
                    MonitorCanvas.Children.Add(new Line
                    {
                        X1 = ox + a.GridX * CellStepX + CardWidth / 2,
                        Y1 = oy + a.GridY * CellStepY + CardHeight / 2,
                        X2 = ox + b.GridX * CellStepX + CardWidth / 2,
                        Y2 = oy + b.GridY * CellStepY + CardHeight / 2,
                        Stroke = new SolidColorBrush(WpfColor.FromRgb(59, 130, 246)),
                        StrokeThickness = 2.5,
                        StrokeDashArray = [4, 3],
                        Opacity = 0.65,
                    });
                }
            }
        }

        _snapGhost = new Border
        {
            Width = CardWidth,
            Height = CardHeight,
            CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(WpfColor.FromRgb(56, 189, 248)),
            BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(WpfColor.FromArgb(45, 56, 189, 248)),
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "Soltar aqui",
                Foreground = new SolidColorBrush(WpfColor.FromRgb(186, 230, 253)),
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        WpfPanel.SetZIndex(_snapGhost, 5);
        MonitorCanvas.Children.Add(_snapGhost);

        foreach (var node in _nodes)
        {
            var card = CreateMonitorCard(node);
            Canvas.SetLeft(card, ox + node.GridX * CellStepX);
            Canvas.SetTop(card, oy + node.GridY * CellStepY);
            WpfPanel.SetZIndex(card, 10);
            MonitorCanvas.Children.Add(card);
        }

        UpdateSummaryLabel();
    }

    private Border CreateMonitorCard(DeviceNode node)
    {
        var card = new Border
        {
            Width = CardWidth,
            Height = CardHeight,
            Background = new SolidColorBrush(node.IsMaster ? WpfColor.FromRgb(23, 37, 68) : WpfColor.FromRgb(24, 30, 44)),
            BorderBrush = new SolidColorBrush(node.IsMaster ? WpfColor.FromRgb(59, 130, 246) : WpfColor.FromRgb(55, 65, 81)),
            BorderThickness = new Thickness(node.IsMaster ? 2.5 : 1.5),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Cursor = WpfCursors.SizeAll,
            Tag = node,
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var topRow = new Grid();
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var roleBadge = new Border
        {
            Background = new SolidColorBrush(node.IsMaster ? WpfColor.FromRgb(37, 99, 235) : WpfColor.FromRgb(55, 65, 81)),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 2, 7, 2),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = node.IsMaster ? "★ MASTER (Mouse/Teclado)" : "SLAVE (Controlado)",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = WpfBrushes.White,
            },
        };
        Grid.SetColumn(roleBadge, 0);
        topRow.Children.Add(roleBadge);

        if (!node.IsLocal)
        {
            var rmBtn = new System.Windows.Controls.Button
            {
                Content = "✕",
                FontSize = 10,
                Padding = new Thickness(5, 1, 5, 1),
                Style = (Style)FindResource("ModernButton"),
            };
            rmBtn.Click += (_, e) =>
            {
                e.Handled = true;
                _nodes.Remove(node);
                EnsureExactlyOneMaster();
                UnsavedHintText.Visibility = Visibility.Visible;
                RenderCanvas();
            };
            Grid.SetColumn(rmBtn, 1);
            topRow.Children.Add(rmBtn);
        }
        else
        {
            var localLbl = new TextBlock
            {
                Text = "Este PC",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(WpfColor.FromRgb(56, 189, 248)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(localLbl, 1);
            topRow.Children.Add(localLbl);
        }
        Grid.SetRow(topRow, 0);
        grid.Children.Add(topRow);

        var center = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        };
        center.Children.Add(new TextBlock
        {
            Text = node.Name,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = WpfBrushes.White,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        });
        var ipText = string.IsNullOrWhiteSpace(node.IpAddress) ? "IP aguardando" : node.IpAddress;
        center.Children.Add(new TextBlock
        {
            Text = $"{ipText}  •  {node.Width}×{node.Height}",
            FontSize = 11,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(156, 163, 175)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
        });
        Grid.SetRow(center, 1);
        grid.Children.Add(center);

        var roleBtn = new System.Windows.Controls.Button
        {
            Content = node.IsMaster ? "✓ Computador Master" : "★ Definir como Master",
            Style = (Style)FindResource(node.IsMaster ? "ModernButton" : "PrimaryButton"),
            Padding = new Thickness(8, 3, 8, 3),
            FontSize = 11,
            IsEnabled = !node.IsMaster,
        };
        roleBtn.Click += (_, e) =>
        {
            e.Handled = true;
            foreach (var n in _nodes) n.IsMaster = ReferenceEquals(n, node);
            UnsavedHintText.Visibility = Visibility.Visible;
            RenderCanvas();
            SetStatus($"'{node.Name}' definido como MASTER. Clique em 'Aplicar nos Computadores' para sincronizar.");
        };
        Grid.SetRow(roleBtn, 2);
        grid.Children.Add(roleBtn);

        card.Child = grid;
        card.MouseLeftButtonDown += OnCardMouseDown;
        return card;
    }

    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep && IsInsideButton(dep))
            return;
        if (sender is not Border card || card.Tag is not DeviceNode node || _nodes.Count < 2)
            return;

        _draggingNode = node;
        _draggingCard = card;
        var pt = e.GetPosition(MonitorCanvas);
        _dragOffset = new WpfPoint(pt.X - Canvas.GetLeft(card), pt.Y - Canvas.GetTop(card));
        WpfPanel.SetZIndex(card, 100);
        card.Opacity = 0.88;
        MonitorCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_draggingNode == null || _draggingCard == null) return;

        var pt = e.GetPosition(MonitorCanvas);
        var left = pt.X - _dragOffset.X;
        var top = pt.Y - _dragOffset.Y;
        Canvas.SetLeft(_draggingCard, left);
        Canvas.SetTop(_draggingCard, top);

        var cx = left + CardWidth / 2;
        var cy = top + CardHeight / 2;
        var (ox, oy) = GetCanvasOrigin();

        DeviceNode? nearest = null;
        var best = double.MaxValue;
        foreach (var other in _nodes)
        {
            if (ReferenceEquals(other, _draggingNode)) continue;
            var tx = ox + other.GridX * CellStepX + CardWidth / 2;
            var ty = oy + other.GridY * CellStepY + CardHeight / 2;
            var d = double.Hypot(cx - tx, cy - ty);
            if (d < best) { best = d; nearest = other; }
        }

        if (nearest != null && _snapGhost != null)
        {
            var tx = ox + nearest.GridX * CellStepX + CardWidth / 2;
            var ty = oy + nearest.GridY * CellStepY + CardHeight / 2;
            var dx = cx - tx;
            var dy = cy - ty;

            int gx = nearest.GridX, gy = nearest.GridY;
            string label;
            if (Math.Abs(dx) < CardWidth * 0.35 && Math.Abs(dy) < CardHeight * 0.35)
                label = $"⇄ Trocar lado com {nearest.Name}";
            else if (Math.Abs(dx) >= Math.Abs(dy))
            {
                if (dx >= 0) { gx = nearest.GridX + 1; label = $"Direita de {nearest.Name} →"; }
                else { gx = nearest.GridX - 1; label = $"← Esquerda de {nearest.Name}"; }
            }
            else
            {
                if (dy >= 0) { gy = nearest.GridY + 1; label = $"↓ Abaixo de {nearest.Name}"; }
                else { gy = nearest.GridY - 1; label = $"↑ Acima de {nearest.Name}"; }
            }

            _snapTarget = (gx, gy);
            Canvas.SetLeft(_snapGhost, ox + gx * CellStepX);
            Canvas.SetTop(_snapGhost, oy + gy * CellStepY);
            if (_snapGhost.Child is TextBlock tb) tb.Text = label;
            _snapGhost.Visibility = Visibility.Visible;
        }
    }

    private void OnCanvasMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void OnCanvasMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_draggingNode != null && e.LeftButton == MouseButtonState.Released)
            EndDrag();
    }

    private void EndDrag()
    {
        if (_draggingNode == null) return;
        var dragged = _draggingNode;
        var snap = _snapTarget;
        _draggingNode = null;
        _draggingCard = null;
        _snapTarget = null;
        MonitorCanvas.ReleaseMouseCapture();

        if (snap is var (gx, gy) && (dragged.GridX != gx || dragged.GridY != gy))
        {
            var occ = _nodes.FirstOrDefault(n => !ReferenceEquals(n, dragged) && n.GridX == gx && n.GridY == gy);
            if (occ != null)
            {
                occ.GridX = dragged.GridX;
                occ.GridY = dragged.GridY;
            }
            dragged.GridX = gx;
            dragged.GridY = gy;
            NormalizeGrid();
            UnsavedHintText.Visibility = Visibility.Visible;
        }
        RenderCanvas();
    }

    private void NormalizeGrid()
    {
        var anchor = _nodes.FirstOrDefault(n => n.IsMaster) ?? _nodes.FirstOrDefault();
        if (anchor == null) return;
        int ax = anchor.GridX, ay = anchor.GridY;
        foreach (var n in _nodes) { n.GridX -= ax; n.GridY -= ay; }
    }

    private (double Ox, double Oy) GetCanvasOrigin()
    {
        var minX = _nodes.Select(n => n.GridX).DefaultIfEmpty(0).Min();
        var maxX = _nodes.Select(n => n.GridX).DefaultIfEmpty(0).Max();
        var minY = _nodes.Select(n => n.GridY).DefaultIfEmpty(0).Min();
        var maxY = _nodes.Select(n => n.GridY).DefaultIfEmpty(0).Max();

        var totalW = (maxX - minX) * CellStepX + CardWidth;
        var totalH = (maxY - minY) * CellStepY + CardHeight;
        return ((MonitorCanvas.ActualWidth - totalW) / 2 - minX * CellStepX,
                (MonitorCanvas.ActualHeight - totalH) / 2 - minY * CellStepY);
    }

    private void DrawCanvasGrid()
    {
        var brush = new SolidColorBrush(WpfColor.FromArgb(18, 148, 163, 184));
        for (double x = 32; x < MonitorCanvas.ActualWidth; x += 32)
            MonitorCanvas.Children.Add(new Line { X1 = x, Y1 = 0, X2 = x, Y2 = MonitorCanvas.ActualHeight, Stroke = brush, StrokeThickness = 1 });
        for (double y = 32; y < MonitorCanvas.ActualHeight; y += 32)
            MonitorCanvas.Children.Add(new Line { X1 = 0, Y1 = y, X2 = MonitorCanvas.ActualWidth, Y2 = y, Stroke = brush, StrokeThickness = 1 });
    }

    private void UpdateSummaryLabel()
    {
        var master = _nodes.FirstOrDefault(n => n.IsMaster);
        if (master == null) { LayoutSummaryText.Text = "Layout vazio"; return; }

        var parts = new List<string> { $"Master: {master.Name}" };
        foreach (var s in _nodes.Where(n => !ReferenceEquals(n, master)))
        {
            int dx = s.GridX - master.GridX, dy = s.GridY - master.GridY;
            var side = Math.Abs(dx) >= Math.Abs(dy) ? (dx >= 0 ? "Direita" : "Esquerda") : (dy >= 0 ? "Abaixo" : "Acima");
            parts.Add($"{side}: {s.Name} (Slave)");
        }
        LayoutSummaryText.Text = string.Join("   •   ", parts);
    }

    private void EnsureExactlyOneMaster()
    {
        if (_nodes.Count == 0) return;
        if (_nodes.Count(n => n.IsMaster) != 1)
        {
            foreach (var n in _nodes) n.IsMaster = false;
            (_nodes.FirstOrDefault(n => n.IsLocal) ?? _nodes[0]).IsMaster = true;
        }
    }

    private void CycleMasterNode()
    {
        var idx = _nodes.FindIndex(n => n.IsMaster);
        var next = (idx + 1) % _nodes.Count;
        for (var i = 0; i < _nodes.Count; i++) _nodes[i].IsMaster = (i == next);
        UnsavedHintText.Visibility = Visibility.Visible;
        RenderCanvas();
    }

    private static bool IsInsideButton(DependencyObject? dep)
    {
        while (dep != null)
        {
            if (dep is System.Windows.Controls.Primitives.ButtonBase) return true;
            dep = VisualTreeHelper.GetParent(dep);
        }
        return false;
    }

    // ==================== UI ACTIONS ====================

    private void OnMonitorCanvasSizeChanged(object sender, SizeChangedEventArgs e) => RenderCanvas();

    private void OnRecenterCanvasClick(object sender, RoutedEventArgs e)
    {
        NormalizeGrid();
        RenderCanvas();
    }

    private void OnSwapMasterSlaveClick(object sender, RoutedEventArgs e)
    {
        if (_nodes.Count < 2)
        {
            SetStatus("Adicione um segundo computador no layout para inverter Master e Slave.", isWarning: true);
            return;
        }
        CycleMasterNode();
        SetStatus("Master ↔ Slave invertido! Clique em 'Aplicar nos Computadores' para ativar.");
    }

    private async void OnApplyLayoutClick(object sender, RoutedEventArgs e) => await ApplyLayoutInternalAsync();

    private async Task ApplyLayoutInternalAsync()
    {
        ApplyLayoutButton.IsEnabled = false;
        try
        {
            var blockFs = FullscreenGuardCheck.IsChecked == true;
            var (synced, warnings) = await _coordinator.ApplyVisualLayoutAsync(_nodes, blockFs);
            UnsavedHintText.Visibility = Visibility.Collapsed;
            RefreshStatusAndLan();

            if (warnings.Count > 0)
                SetStatus($"Aplicado localmente ({synced} remoto). Aviso: {warnings[0]}", isWarning: true);
            else
                SetStatus($"Layout e papéis Master/Slave sincronizados com sucesso ({synced + 1} computador(es))!");
        }
        catch (Exception ex)
        {
            SetStatus($"Erro ao aplicar layout: {ex.Message}", isWarning: true);
        }
        finally
        {
            ApplyLayoutButton.IsEnabled = true;
        }
    }

    private void OnRegeneratePinClick(object sender, RoutedEventArgs e)
    {
        PinCodeText.Text = _coordinator.Store.RegeneratePin();
        SetStatus("Novo PIN de 4 dígitos gerado.");
    }

    private async void OnScanLanClick(object sender, RoutedEventArgs e)
    {
        await _coordinator.Lan.BroadcastProbeAsync();
        await Task.Delay(300);
        RefreshStatusAndLan();
        SetStatus("Busca de dispositivos na rede local atualizada.");
    }

    private async void OnManualPairClick(object sender, RoutedEventArgs e) =>
        await PairDeviceAsync(ManualIpBox.Text.Trim(), _coordinator.Store.GetSnapshot().ControlPort, ManualPinBox.Text.Trim());

    private async Task PairDeviceAsync(string ip, int port, string pin)
    {
        if (string.IsNullOrWhiteSpace(ip) || pin.Length < 4)
        {
            SetStatus("Informe o IP e o PIN de 4 dígitos exibido no outro computador.", isWarning: true);
            return;
        }

        try
        {
            SetStatus($"Pareando com {ip}...");
            var resp = await _coordinator.Lan.PairWithDeviceAsync(ip, port, pin);

            var existing = _nodes.FirstOrDefault(n => string.Equals(n.Name, resp.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                var maxX = _nodes.Select(n => n.GridX).DefaultIfEmpty(0).Max();
                _nodes.Add(new DeviceNode
                {
                    Name = resp.DeviceName,
                    IpAddress = ip,
                    Width = resp.Width,
                    Height = resp.Height,
                    GridX = maxX + 1,
                    GridY = 0,
                    IsMaster = false,
                    IsLocal = false,
                });
                UnsavedHintText.Visibility = Visibility.Visible;
            }
            else
            {
                existing.IpAddress = ip;
            }

            RenderCanvas();
            RefreshStatusAndLan();
            SetStatus($"Pareado com '{resp.DeviceName}' ({ip})! Clique em 'Aplicar nos Computadores' para iniciar a transição.");
        }
        catch (Exception ex)
        {
            SetStatus($"Erro ao parear com {ip}: {ex.Message}", isWarning: true);
        }
    }

    private void OnFullscreenGuardChanged(object sender, RoutedEventArgs e)
    {
        UnsavedHintText.Visibility = Visibility.Visible;
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        try
        {
            var enable = AutoStartCheck.IsChecked == true;
            _coordinator.SetAutoStart(enable);
            SetStatus(enable ? "MultiSync configurado para iniciar junto com o Windows." : "Inicialização automática removida.");
        }
        catch (Exception ex)
        {
            SetStatus($"Não foi possível alterar inicialização automática: {ex.Message}", isWarning: true);
        }
    }

    private void OnOpenFirewallClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _coordinator.OpenFirewallPorts();
            SetStatus("Solicitação de regras enviada ao Firewall do Windows (confirme o UAC).");
        }
        catch (Exception ex)
        {
            SetStatus($"Erro ao abrir Firewall: {ex.Message}", isWarning: true);
        }
    }

    private async void OnAudioToggleClick(object sender, RoutedEventArgs e)
    {
        AudioToggleButton.IsEnabled = false;
        try
        {
            if (_coordinator.Audio.Mode == AudioShareMode.Off)
            {
                var (started, warnings) = await _coordinator.EnableReceiveAudioHereAsync();
                if (started > 0)
                {
                    SetStatus($"🎧 Áudio compartilhado ativado! Ouvindo {started} computador(es) neste fone.");
                }
                else
                {
                    var warn = warnings.FirstOrDefault() ?? "Não foi possível iniciar o áudio compartilhado.";
                    SetStatus(warn, isWarning: true);
                }
            }
            else
            {
                await _coordinator.DisableSharedAudioAsync();
                SetStatus("Áudio compartilhado desligado nos dois computadores.");
            }
            RefreshAudioUi();
        }
        catch (Exception ex)
        {
            SetStatus($"Erro no áudio compartilhado: {ex.Message}", isWarning: true);
        }
        finally
        {
            AudioToggleButton.IsEnabled = true;
        }
    }

    private void OnAudioMuteClick(object sender, RoutedEventArgs e)
    {
        _coordinator.Audio.IsMuted = !_coordinator.Audio.IsMuted;
        RefreshAudioUi();
    }

    private void OnAudioVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AudioVolumeText == null || _coordinator == null) return;
        var pct = (int)Math.Round(e.NewValue);
        AudioVolumeText.Text = $"{pct}%";
        _coordinator.Audio.Volume = (float)(e.NewValue / 100.0);
    }

    private void SetStatus(string msg, bool isWarning = false)
    {
        FeedbackStatusText.Text = msg;
        FeedbackStatusText.Foreground = new SolidColorBrush(isWarning
            ? WpfColor.FromRgb(251, 191, 36)
            : WpfColor.FromRgb(209, 213, 219));
    }
}
