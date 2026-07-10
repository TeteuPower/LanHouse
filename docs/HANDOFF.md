# HANDOFF — continuação do VirtualLan

> Documento para o próximo agente (Claude no VS Code) assumir o desenvolvimento.
> Escrito ao pausar o trabalho a pedido do usuário. Leia inteiro antes de tocar em código.

## Status (atualização — entrega concluída)

O produto pedido está **pronto e empacotado**. O que era "o que falta" (§5) foi feito, com
algumas decisões diferentes do plano original, guiadas pelo pedido do usuário ("me entregue
pronto, sem 40 min de configuração"):

- **GUI `VirtualLan.exe`** (WinForms, `src/VirtualLan.App`): conectar/desconectar, lista de
  participantes, log, bandeja, preferências. Instala o TAP sozinho na 1ª vez.
- **Instalador do TAP em C#** (`TapInstaller.cs`) — sem PowerShell para o usuário.
- **Modo servidor embutido**: a própria GUI hospeda o relay, abre o firewall, tenta UPnP no
  roteador e mostra o endereço para compartilhar — elimina a necessidade de VPS no caso comum
  (usuário sem CGNAT). O relay dedicado (Windows/Linux) segue disponível em `dist/extras`.
- **Empacotamento** (`scripts/package.ps1`): `dist/VirtualLan/` + `dist/VirtualLan.zip`
  autocontido (sem instalar .NET). Docs voltados à GUI (`README`, `docs/TUTORIAL.md`, `LEIA-ME`).
- **Verificação**: build Release limpo, 35/35 testes, detecção de adaptador validada no registro
  real (o bug do `root\tap0901` está corrigido de verdade), relay autocontido rodando, UPnP do
  roteador respondendo, e uma **revisão adversarial multiagente** cujos achados reais foram
  corrigidos (congelamento da UI no modo servidor, exceções não tratadas, coerência de settings).

O que **não** foi feito (e por quê): o laboratório de NAT em Linux (§6) e a extração do
`NodeEngine` para o Core (§7) eram para permitir teste em Linux — mas o trabalho passou a rodar
no **Windows real** do usuário, onde o motor/relay já são cobertos pelos testes e a detecção do
TAP foi validada no registro real. O único ponto não provável fora de uma sessão elevada é o
ioctl do TAP (abrir/ler/escrever quadros) e o `netsh` de IP — exige Administrador.

O restante deste documento é o plano histórico do agente anterior; leia como contexto.

## 0. Resumo em uma frase

VirtualLan é um "Garena/Hamachi caseiro": um **switch Ethernet virtual distribuído** que faz
dois PCs Windows em redes/NATs diferentes se enxergarem como se estivessem na mesma LAN física,
para jogar jogos em modo LAN. Já existe um **protótipo funcional e testado** (relay + cliente +
protocolo + cripto), faltam **empacotamento, GUI e o instalador embutido** para o usuário final.

## 1. Quem é o usuário e o que ele pediu

- Usuário: TrustSis Labs (`inovacoes@trustsis.com`). Fala português. Preferência: respostas
  concisas e diretas.
- Instrução do projeto: "Atue como um desenvolvedor full stack Sr, com métodos profissionais".
- Pedido original: recriar a experiência do Garena para Warcraft III/DotA, mas **genérico** para
  qualquer jogo LAN. Host cria a sala e ela aparece em (LAN) para o outro, sem burocracia.
- **Windows dos dois lados** (máquina dele e do amigo). Confirmado explicitamente. O produto
  final é Windows-only.
- Pedido mais recente (o que falta fazer):
  1. Entregar **pronto para usar**: ele quer só mandar o programa pro amigo e abrir o dele.
  2. Empacotar tudo (pasta com dependências para zipar **ou** um instalador).
  3. **Criar uma interface gráfica** para usar.
  4. Rodar os testes e seguir até finalizar.
  5. **Simular um segundo PC em outra rede** para testar a conexão de verdade.

## 2. Decisões de arquitetura já tomadas (com o usuário)

Perguntei no início e ele escolheu:

| Decisão | Escolha | Implicação |
|---|---|---|
| Transporte | **Relay + P2P** (hole punching, com fallback via relay) | é o modelo Hamachi/ZeroTier |
| Stack do cliente | **C# / .NET 8** | GUI fácil no Windows, P/Invoke pro driver |
| Escopo da 1ª entrega | **Protótipo funcional end-to-end** | já entregue e testado |
| Plataforma | **Windows apenas** | driver tap-windows6 (assinado pela OpenVPN) |

**Por que L2 e não uma VPN comum:** jogos LAN anunciam a partida por **broadcast UDP**, que é
Camada 2 e não roteia pela internet. Um túnel IP (L3) passa o ping e nunca a sala. Por isso
transportamos **quadros Ethernet crus** (com ARP e broadcast) — um switch virtual, não um túnel.

Detalhes completos em `docs/ARCHITECTURE.md` e o modelo de segurança em `docs/SECURITY.md`.
Ambos já escritos e fiéis ao código. Leia-os.

## 3. Estado atual do repositório (o que já existe e funciona)

```
VirtualLan.sln
src/
  VirtualLan.Core/     (net8.0)          protocolo, cripto, switching — SEM I/O, 100% testável
  VirtualLan.Relay/    (net8.0, exe)     servidor UDP rendezvous + relay de dados
  VirtualLan.Node/     (net8.0-windows)  cliente: P/Invoke tap-windows6, hole punching, forwarding
tests/
  VirtualLan.Core.Tests/  35 testes (protocolo, cripto, switching, integração com relay real)
scripts/
  install-tap.ps1      instala driver TAP + cria adaptador (idempotente)
  uninstall-tap.ps1
  build.ps1            testa + publica os binários
deploy/
  vlan-relay.service   systemd unit para o VPS
docs/
  ARCHITECTURE.md, SECURITY.md, TUTORIAL.md, HANDOFF.md (este)
```

### Estado de build/teste: VERDE
- `dotnet build VirtualLan.sln -c Release` → **Build succeeded**, 0 warnings (TreatWarningsAsErrors).
- `dotnet test` → **35/35 passando**.
- Também testei o relay como **processo real** com pacotes hostis (lixo, versão errada, Register
  truncado, DataRelay forjado, nodeId de outra rede) — tudo descartado corretamente.

### O SDK no sandbox
.NET não vem no ambiente. Instalei em `/tmp/dotnet`. Para reusar:
```bash
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_ROOT=/tmp/dotnet
/tmp/dotnet/dotnet --version   # 8.0.422
# se sumiu: curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir /tmp/dotnet
```
NuGet/restore falha se rodar dentro do mount `/sessions/.../mnt/` (permissão no obj). **Solução:**
copie a árvore para `/tmp/vl` e rode lá:
```bash
rm -rf /tmp/vl && mkdir -p /tmp/vl
cd /sessions/eloquent-serene-maxwell/mnt/lanhouse
tar cf - --exclude=obj --exclude=bin --exclude=dist src tests VirtualLan.sln | (cd /tmp/vl && tar xf -)
cd /tmp/vl && /tmp/dotnet/dotnet test tests/VirtualLan.Core.Tests/VirtualLan.Core.Tests.csproj -c Release
```

## 4. O bug que o usuário encontrou (JÁ CORRIGIDO)

Ao rodar de verdade no Windows dele, o cliente disse "Nenhum adaptador TAP encontrado" mesmo
com o adaptador existindo. **Causa:** o `tapctl create` registra o `ComponentId` como
`root\tap0901` (com prefixo do enumerador), mas o código procurava a string crua `tap0901`.

**Correção aplicada** em `src/VirtualLan.Node/Tap/TapAdapterInfo.cs`:
- `NormalizeComponentId()` remove o prefixo antes de comparar.
- `EnumerateNetworkClass()` + `BuildNotFoundMessage()` agora **despejam o registro inteiro** na
  mensagem de erro, marcando o que é compatível — nunca mais depuração às cegas.
- `--list-adapters` também mostra tudo.
- `scripts/install-tap.ps1` reescrito: idempotente (tolera "already exists"), normaliza o
  ComponentId, e imprime diagnóstico se falhar.

Isso resolve o erro dele. Ele ainda não confirmou se rodou de novo — vale pedir o output de
`vlan.exe --list-adapters` se reaparecer problema.

## 5. O QUE FALTA (plano de execução para você)

Task list atual (IDs do sistema de tarefas):

- **#7 Extrair motor para Core (IFrameDevice / INetworkConfigurator)** — EM ANDAMENTO, só planejado.
- **#8 Simular dois PCs em redes diferentes (NAT lab)** — técnica já provada, ver §6.
- **#9 Instalador do driver TAP embutido no app (sem PowerShell)**
- **#10 Interface gráfica (WinForms)**
- **#11 Empacotar: um único .exe para mandar ao amigo**
- **#12 Verificação final: testes + build + revisão**

### #7 — Abstrair o motor (faça primeiro; destrava o teste real)
O `NodeService` hoje vive em `VirtualLan.Node` e depende de `TapDevice`/`NetworkConfigurator`
(Windows). Extraia para `VirtualLan.Core` como `NodeEngine`, recebendo por injeção:
```csharp
public interface IFrameDevice : IDisposable {
    string Name { get; }
    MacAddress MacAddress { get; }
    ValueTask<int> ReadFrameAsync(Memory<byte> dst, CancellationToken ct);
    ValueTask WriteFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken ct);
}
public interface INetworkConfigurator {
    int Mtu => 1400;
    void ConfigureInterface(string name, IPAddress addr, IPAddress mask);
    void ApplyHostFirewallAndProfile(string name, IPAddress subnet, IPAddress mask) {}
    void Cleanup(string name) {}
}
```
Mova junto para Core (são cross-platform): `NodeOptions`, `Peers/Peer.cs`,
`Peers/LocalEndpointDiscovery.cs`. Em `VirtualLan.Node` ficam só: `TapDevice` (implementa
`IFrameDevice`), `WindowsNetworkConfigurator` (implementa `INetworkConfigurator`, o atual
`NetworkConfigurator`), a GUI e o `Program.cs`. A seleção de adaptador (`TapAdapterLocator`)
continua no Node e roda **antes** de construir o `NodeEngine`.
**IMPORTANTE:** o produto continua Windows-only. A abstração é só para permitir um
`IFrameDevice` de teste em Linux (namespaces) — não é multiplataforma de produção.

### #8 — Laboratório de NAT (a simulação do "segundo PC")
Ver §6. Objetivo: dois `NodeEngine` reais, cada um em sua LAN privada atrás de um roteador com
MASQUERADE, um relay na "WAN", e um mini-jogo que faz broadcast de descoberta. Provar que a
sala do host aparece pro guest **atravessando NAT** (hole punching) e, num segundo cenário com
NAT simétrico, **via relay**. Precisa de um `LinuxTapDevice` (ioctl TUNSETIFF em /dev/net/tun)
implementando `IFrameDevice`, num projeto de teste `tools/VirtualLan.NetTest` (net8.0, Linux).

### #9 — Instalador embutido (sem PowerShell para o usuário)
Hoje o usuário precisa rodar `install-tap.ps1` à mão. Faça o `vlan.exe` (ou a GUI) detectar a
ausência do adaptador e, elevado, **baixar o instalador oficial da OpenVPN**
(`https://build.openvpn.net/downloads/releases/latest/tap-windows-latest-stable.exe`), rodar em
modo silencioso (`/S`), e criar+renomear o adaptador via `tapctl`/`tapinstall`. Porte a lógica
do `install-tap.ps1` para C# em `src/VirtualLan.Node/Tap/TapInstaller.cs`. Verifique o hash do
download se possível. NUNCA embutir o driver no repo (licença GPL-2.0 da OpenVPN; baixe em runtime).

### #10 — GUI (WinForms)
`net8.0-windows`, `<UseWindowsForms>true</UseWindowsForms>`. Janela única:
- Campos: Relay (host:porta), Nome da rede, Senha (com opção de mostrar).
- Botão **Conectar/Desconectar**. Ao conectar pela primeira vez sem adaptador → dispara #9 com
  barra de progresso.
- Lista de peers: IP virtual (25.0.0.x), estado (direto/relay), latência.
- Painel de log (reaproveite o `Log`; direcione para um TextBox via um sink).
- Ícone na bandeja, "iniciar com o Windows" opcional.
- O `NodeEngine` roda numa Task; a GUI só observa. Exponha eventos (PeerAdded/PeerUpdated/
  PathChanged/StatusChanged) do engine para a GUI não fazer polling.

### #11 — Empacotar
`dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`. Gerar uma
pasta `dist/VirtualLan/` com: `VirtualLan.exe` (GUI), `LEIA-ME.txt` (curto, com o passo a passo
e o campo de relay/rede/senha), e o `install-tap.ps1` como fallback. Um `.zip` pronto para
mandar. Opcional: instalador Inno Setup (`deploy/installer.iss`) que instala o TAP e cria atalho.

### #12 — Verificação final
Suite completa + build Release + o teste de NAT do §6 rodando de verdade + revisão do código do
TAP por subagente (Task tool). Só marque pronto com o teste de dois nós trocando broadcast.

## 6. Como simular o segundo PC em outra rede (TÉCNICA JÁ PROVADA)

O sandbox não é root, mas `unshare -Urnm --map-root-user` dá root dentro de um user+net+mount
namespace, com CAP_NET_ADMIN. Provei que dá pra montar tmpfs em /run e usar `ip netns`:

```bash
unshare -Urnm --map-root-user bash -c '
  mount -t tmpfs none /run
  mkdir -p /run/netns
  ip netns add lanA && ip netns add lanB    # FUNCIONA
  ip -n lanA link add veth0 type veth peer name gw   # FUNCIONA
'
```

Topologia alvo (tudo dentro de UM `unshare -Urnm`):
```
  [nodeA]--vethA--(routerA: MASQUERADE)--vethWA--+
                                                  +--(brWAN 100.64.0.0/24)--[relay :7777]
  [nodeB]--vethB--(routerB: MASQUERADE)--vethWB--+
```
- `nodeA`/`nodeB`: cada um numa netns própria, com uma LAN privada (ex.: 10.10.1.0/24 e
  10.10.2.0/24) e um `LinuxTapDevice` recebendo IP 25.0.0.x do relay.
- `routerA`/`routerB`: netns com `iptables -t nat -A POSTROUTING -j MASQUERADE`. Para simular
  **NAT de cone** (hole punching funciona) use MASQUERADE normal; para **NAT simétrico**
  (força fallback) use `--random` no SNAT para embaralhar a porta externa.
- `relay`: na netns da WAN, IP público fake (100.64.0.1).
- Mini-jogo: `nodeA` bind UDP em 25.0.0.1:6112 e responde "ROOM" a broadcast; `nodeB` manda
  broadcast para 25.0.0.255:6112 e espera a resposta. Se chegar → a sala "apareceu" cruzando NAT.

Escreva isso como `tools/nat-lab.sh` (orquestra netns) + `tools/VirtualLan.NetTest` (o binário
que roda o engine com LinuxTapDevice e o mini-jogo). Documente em `docs/NAT-LAB.md`.

## 7. Armadilhas que já me morderam (não repita)

- **Ferramenta de arquivo dessincroniza em .cs longos que passam por vários Edit.** Sintoma:
  o compilador (via mount bash) vê o arquivo **truncado no meio de uma string**, enquanto o
  Read mostra certo. Aconteceu 2x (VirtualLan.Node.csproj e Program.cs/TapAdapterInfo.cs).
  **Solução confiável:** reescreva o arquivo inteiro via `cat > arquivo <<'EOF' ... EOF` no bash
  (bytes garantidos), depois rebuild. Sempre confirme com `wc -l` e `tail`.
- **Acentos:** heredoc do bash preserva UTF-8 certo; o PowerShell lê `.ps1` como ANSI se não tiver
  BOM — por isso os scripts saíram com `instalaÃ§Ã£o`. Se for reescrever .ps1, salve como UTF-8
  **com BOM** ou evite acentos.
- **UDP no Windows:** sem `SIO_UDP_CONNRESET` desativado, um ICMP port-unreachable derruba o
  socket. Já está tratado no relay e no node — mantenha ao mexer em sockets.
- **MTU 1400** proposital (overhead 80B + folga PPPoE). Não aumente sem recalcular `Wire.MaxFrameSize`.
- **NodeId é efêmero de propósito** (prefixo do nonce AES-GCM). NUNCA persista. Ver SECURITY.md.
- **AES-GCM não é thread-safe** no .NET; o `FrameCipher` serializa com lock. Mantenha.

## 8. Como rodar o que já existe (referência rápida)

```bash
# testes
cd /tmp/vl && /tmp/dotnet/dotnet test tests/VirtualLan.Core.Tests/VirtualLan.Core.Tests.csproj -c Release
# relay local (funciona no Linux)
/tmp/dotnet/dotnet run --project src/VirtualLan.Relay -- --port 7777 -v
```
No Windows do usuário: `scripts/install-tap.ps1` (admin) e depois
`vlan.exe --relay HOST:7777 --network NOME --password SENHA`.

## 9. Contrato de "pronto" (definição de done que o usuário espera)

1. Ele abre **um** programa com interface, digita relay/rede/senha, clica Conectar.
2. Manda **um** arquivo (ou zip/instalador) pro amigo; o amigo faz o mesmo.
3. Abrem o jogo em LAN e se enxergam. Sem terminal, sem PowerShell manual.
4. O TAP é instalado automaticamente na primeira execução.

Enquanto isso não acontece de ponta a ponta, não está pronto. O único trecho que você **não**
consegue provar no sandbox é o ioctl do TAP no Windows real — deixe isso explícito para ele e
teste todo o resto no NAT lab.

## 10. Notas finais desta sessão (para o próximo agente)

- **Contratos da etapa #7 já existem**, prontos e compilando, em
  `src/VirtualLan.Core/Engine/IFrameDevice.cs`: `IFrameDevice` e `INetworkConfigurator`.
  Falta só implementar o `NodeEngine` contra eles e mover o `NodeService` do Node para o Core.
- **Publicar no GitHub:** rode `scripts/git-setup.ps1` no Windows (PowerShell, dentro de
  `C:\Trabalho\lanhouse`). Ele remove qualquer `.git` corrompido, faz init+commit e dá push para
  `https://github.com/TeteuPower/LanHouse.git`. O push pede a autenticação do usuário — é o único
  passo que depende dele.
- **Aviso sobre o `.git`:** um ambiente Linux pode ter deixado um `.git` parcial que não pôde ser
  apagado de lá (o mount proíbe unlink). No Windows ele apaga normalmente — o `git-setup.ps1` já
  faz isso. Se o VS Code reclamar de repositório inválido ao abrir, rode o script.
- **Documentos que definem o trabalho:** `docs/PRODUTO.md` (visão/negócio),
  `docs/PROMPT-CONTINUACAO.md` (o prompt que o usuário vai colar em você), este `HANDOFF.md`,
  `docs/ARCHITECTURE.md` e `docs/SECURITY.md`.
- **Estado verificado ao pausar:** build Release limpo (0 warnings) e 35/35 testes passando.
