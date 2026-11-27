#!/usr/bin/env bash
set -euo pipefail

# MultiSync Linux Installer — Debian, Linux Mint, Ubuntu & Arch Linux
echo "============================================================"
echo "  ⇄ Instalador MultiSync para Linux (v0.6.0)"
echo "  Suporte: Debian, Linux Mint, Ubuntu e Arch Linux"
echo "============================================================"

DISTRO_ID=""
DISTRO_LIKE=""
if [ -f /etc/os-release ]; then
  # shellcheck disable=SC1091
  source /etc/os-release
  DISTRO_ID="${ID:-}"
  DISTRO_LIKE="${ID_LIKE:-}"
fi

echo "-> Distro detectada: ${PRETTY_NAME:-Linux} (${DISTRO_ID})"

# 1. Instalar dependências do sistema (X11/Wayland clipboard, PulseAudio/PipeWire, ffmpeg, libXtst)
if command -v apt-get >/dev/null 2>&1; then
  echo "-> Instalando pacotes via APT (Debian / Mint / Ubuntu)..."
  sudo apt-get update -y
  sudo apt-get install -y xclip wl-clipboard ffmpeg pulseaudio-utils libxtst6 libx11-6 x11-xserver-utils xdg-utils curl
elif command -v pacman >/dev/null 2>&1; then
  echo "-> Instalando pacotes via Pacman (Arch Linux / Manjaro / EndeavourOS)..."
  sudo pacman -Sy --needed --noconfirm xclip wl-clipboard ffmpeg libpulse libxtst libx11 xorg-xrandr xdg-utils curl
else
  echo "[Aviso] Gerenciador de pacotes não reconhecido. Instale manualmente: xclip, wl-clipboard, ffmpeg, pulseaudio-utils/libpulse, libxtst."
fi

# 2. Configurar /dev/uinput para controle sem sudo (funciona em X11 e Wayland!)
echo "-> Configurando permissão /dev/uinput (udev)..."
sudo modprobe uinput || true
echo 'KERNEL=="uinput", MODE="0660", GROUP="input", OPTIONS+="static_node=uinput"' | sudo tee /etc/udev/rules.d/99-multisync-uinput.rules >/dev/null
echo "uinput" | sudo tee /etc/modules-load.d/multisync-uinput.conf >/dev/null
sudo udevadm control --reload-rules && sudo udevadm trigger || true
if getent group input >/dev/null 2>&1; then
  sudo usermod -aG input "$USER" || true
fi

# 3. Liberar portas no Firewall (ufw ou firewalld, se ativo)
if command -v ufw >/dev/null 2>&1 && sudo ufw status | grep -q "Status: active"; then
  echo "-> Liberando portas MultiSync no UFW..."
  sudo ufw allow 5050/tcp comment "MultiSync KVM" || true
  sudo ufw allow 5051/udp comment "MultiSync Discovery" || true
  sudo ufw allow 5052/tcp comment "MultiSync Control/Files/Screen" || true
  sudo ufw allow 5053/udp comment "MultiSync Shared Audio" || true
elif command -v firewall-cmd >/dev/null 2>&1 && sudo firewall-cmd --state >/dev/null 2>&1; then
  echo "-> Liberando portas MultiSync no Firewalld..."
  sudo firewall-cmd --permanent --add-port=5050/tcp || true
  sudo firewall-cmd --permanent --add-port=5051/udp || true
  sudo firewall-cmd --permanent --add-port=5052/tcp || true
  sudo firewall-cmd --permanent --add-port=5053/udp || true
  sudo firewall-cmd --reload || true
fi

# 4. Compilar ou copiar binário para ~/.local/bin/multisync
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
INSTALL_DIR="$HOME/.local/bin"
mkdir -p "$INSTALL_DIR"

if [ -f "$REPO_ROOT/publish-linux/multisync-linux" ]; then
  echo "-> Instalando binário pré-compilado..."
  cp "$REPO_ROOT/publish-linux/multisync-linux" "$INSTALL_DIR/multisync"
elif command -v dotnet >/dev/null 2>&1; then
  echo "-> Compilando MultiSync.Linux (.NET 10)..."
  dotnet publish "$REPO_ROOT/MultiSync.Linux/MultiSync.Linux.csproj" \
    -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$REPO_ROOT/publish-linux"
  cp "$REPO_ROOT/publish-linux/multisync-linux" "$INSTALL_DIR/multisync"
else
  echo "[Erro] Nem publish-linux/multisync-linux nem o .NET 10 SDK foram encontrados."
  echo "Instale o .NET 10 SDK (https://dot.net) ou copie o binário publish-linux/multisync-linux gerado no Windows."
  exit 1
fi
chmod +x "$INSTALL_DIR/multisync"

# 5. Criar atalho no menu de aplicativos
mkdir -p "$HOME/.local/share/applications"
cat > "$HOME/.local/share/applications/multisync.desktop" <<EOF
[Desktop Entry]
Name=MultiSync
Comment=Controle Multi-PC, Áudio Compartilhado, Arquivos e Tela ao Vivo
Exec=$INSTALL_DIR/multisync
Terminal=false
Type=Application
Categories=Utility;Network;
EOF

# 6. Criar serviço systemd --user (opcional para iniciar com o sistema)
mkdir -p "$HOME/.config/systemd/user"
cat > "$HOME/.config/systemd/user/multisync.service" <<EOF
[Unit]
Description=MultiSync Multi-PC Integration Daemon
After=network-online.target graphical-session.target

[Service]
ExecStart=$INSTALL_DIR/multisync --headless
Restart=on-failure
RestartSec=3

[Install]
WantedBy=default.target
EOF

echo ""
echo "✅ MultiSync instalado em $INSTALL_DIR/multisync!"
echo "   • Para abrir agora com o painel visual:  ~/.local/bin/multisync"
echo "   • Para iniciar automaticamente no login: systemctl --user enable --now multisync"
echo "   • Nota: se foi a primeira vez adicionando seu usuário ao grupo 'input', faça logout/login uma vez para ativar /dev/uinput."
