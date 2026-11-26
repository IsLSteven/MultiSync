# MultiSync

**Plataforma de Integração Multi-PC para Windows** — compartilhe mouse, teclado, área de transferência, áudio e telas entre computadores na mesma rede local com interface visual Drag & Drop.

---

## Principais Recursos

- **Layout Visual Drag & Drop (WPF Nativo):** posicione seus computadores arrastando as telas para Esquerda, Direita, Acima ou Abaixo e alterne dinamicamente quem é o **Master** (teclado/mouse físicos) e quem é o **Slave** com 1 clique.
- **Auto-Descoberta na Rede Local (LAN):** encontra automaticamente outros computadores executando o MultiSync na mesma rede via UDP e realiza pareamento seguro por PIN de 4 dígitos.
- **Transporte Criptografado de Baixa Latência:** comunicação via TLS (`SslStream`) autenticada por HMAC-SHA256 com medição contínua de latência (ping em `ms`).
- **Modo Jogo / Tela Cheia Inteligente:** bloqueia automaticamente a passagem acidental do mouse pela borda quando há um jogo ou aplicativo em Tela Cheia / Sem Moldura em foco (ex.: *League of Legends*), mantendo os atalhos de teclado ativos.
- **Bandeja do Sistema (System Tray):** roda de forma leve perto do relógio do Windows com opção de iniciar junto com o sistema.

---

## Roadmap

- [x] Motor KVM nativo Win32 (mouse, teclado com suporte ABNT2/Unicode, roda de rolagem e área de transferência)
- [x] Proteção automática de borda para jogos em Tela Cheia / Borderless
- [x] Descoberta automática de dispositivos na LAN + Pareamento por PIN
- [x] Interface gráfica em C# / WPF com Drag & Drop de telas e inversão dinâmica Master ↔ Slave
- [ ] Áudio compartilhado em tempo real (WASAPI Loopback → fone único)
- [ ] Visualização cruzada de tela (Picture-in-Picture via DXGI Desktop Duplication)
- [ ] Arrastar e soltar arquivos diretamente entre telas (Cross-Screen Drag & Drop)

---

## Estrutura do Projeto

- `MultiSync.Core/` — Motor Win32 de captura/injeção de entrada, transporte TLS, descoberta LAN e compilador de topologia
- `MultiSync.App/` — Aplicativo Desktop em C# / WPF e ícone de bandeja (System Tray)
- `MultiSync.Tests/` — Testes automatizados de topologia, protocolo e segurança

---

## Como Compilar

Requisitos: **.NET 10 SDK** no Windows 10/11 (x64).

```powershell
dotnet test MultiSync.sln
dotnet publish MultiSync.App -c Release -r win-x64 --self-contained true -o publish
```
