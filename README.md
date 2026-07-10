# VirtualLan

Rede LAN virtual para jogar jogos em modo LAN com amigos que estão em outra rede, em outra
cidade, atrás de outro NAT. O jogo não sabe de nada: ele vê um adaptador Ethernet comum, manda
o broadcast de descoberta de sala, e a sala aparece na lista.

É o mesmo conceito do Garena / Hamachi / Radmin VPN, escrito do zero, genérico para qualquer
jogo que use LAN.

```
   PC A (host)                                        PC B (cliente)
┌──────────────────┐                              ┌──────────────────┐
│  Warcraft III    │                              │  Warcraft III    │
│        ↓ broadcast UDP                          │  ↑ vê a sala     │
│  ┌────────────┐  │                              │  ┌────────────┐  │
│  │ TAP virtual│  │                              │  │ TAP virtual│  │
│  │ 25.0.0.1   │  │                              │  │ 25.0.0.2   │  │
│  └─────┬──────┘  │                              │  └─────▲──────┘  │
│   vlan.exe       │ ══ UDP direto (hole punch) ═▶│   vlan.exe       │
└────────┬─────────┘                              └────────▲─────────┘
         │              ┌──────────────────┐               │
         └─────────────▶│    vlan-relay    │◀──────────────┘
            fallback    │  (seu VPS, UDP)  │   fallback
                        └──────────────────┘
```

## Por que não basta uma VPN comum

Jogos de LAN anunciam a partida com **broadcast UDP**. Broadcast é Camada 2 e não é roteado
pela internet — um túnel IP (Camada 3) entrega o ping mas nunca a sala. Por isso o VirtualLan
implementa um **switch Ethernet virtual distribuído**: ele transporta quadros Ethernet crus,
com ARP e broadcast, exatamente como um switch físico faria.

Detalhes em [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

## Início rápido (com interface)

O produto final é **um** aplicativo com interface gráfica: `VirtualLan.exe`. Ele pede
administrador sozinho e, na primeira vez, instala o adaptador virtual automaticamente.

1. **Um de vocês** abre o app, marca **“Hospedar o relay neste PC (eu sou o servidor)”**,
   escolhe um nome de rede e uma senha, e clica **Conectar**. O app abre a porta no firewall,
   tenta o UPnP no roteador e mostra o **endereço para enviar ao amigo**.
2. **O amigo** abre o app, cola esse endereço em “Servidor do amigo”, digita o mesmo nome de
   rede e a mesma senha, e clica **Conectar**.
3. Abram o jogo em modo **LAN**. A sala do host aparece para o amigo.

Passo a passo completo (e solução de problemas) em [`docs/TUTORIAL.md`](docs/TUTORIAL.md).

## Componentes

| Binário | Onde roda | O quê |
|---|---|---|
| `VirtualLan.exe` | Windows | **A interface.** Cliente + (opcional) relay embutido. É o que você compartilha. |
| `vlan.exe` | Windows, como Admin | Mesma função, em linha de comando (uso avançado/diagnóstico). |
| `vlan-relay` | Windows ou VPS Linux | Relay dedicado, para um servidor sempre ligado (opcional). |

Um relay bem pequeno serve: ele só encaminha tráfego enquanto os peers não conseguem falar
direto. Na maioria dos casos, após 1–2 segundos o tráfego passa a ser P2P e o relay fica ocioso.

## Empacotar para compartilhar

```powershell
.\scripts\package.ps1
```

Gera `dist\VirtualLan\` (com `VirtualLan.exe` autocontido, `LEIA-ME.txt` e o tutorial) e um
`dist\VirtualLan.zip` pronto para mandar ao amigo — ele extrai e abre, sem instalar o .NET. Os
binários avançados (relay dedicado Windows/Linux e o cliente CLI) ficam em `dist\extras\`.

## Instalação

Para o uso normal, **não há instalação manual**: abra o `VirtualLan.exe` e clique Conectar. Na
primeira vez ele instala o driver `tap-windows6` (assinado pela OpenVPN — não escrevemos driver
próprio, isso exigiria certificado EV e atestação da Microsoft) e cria o adaptador `VirtualLan`.

O passo a passo, incluindo o modo servidor e a solução de problemas, está em
[`docs/TUTORIAL.md`](docs/TUTORIAL.md).

## Uso avançado (linha de comando)

Quem preferir terminal pode usar o cliente `vlan.exe` (em `dist\extras\cli-win-x64`), como
Administrador, com o mesmo nome de rede e senha nos dois lados:

```powershell
vlan.exe --relay meu-vps.exemplo.com:7777 --network dota-sexta --password "cavalo bateria grampo correto"
```

```
vlan --relay <host:porta> --network <nome> --password <senha> [opções]

  --adapter, -a <nome>   Adaptador TAP, se houver mais de um.
  --port <n>             Porta UDP local (padrão: efêmera).
  --list-adapters        Lista os adaptadores TAP e sai.
  --install-tap          Instala o driver/adaptador TAP e sai (sem PowerShell).
  --verbose, -v          Log de depuração.
```

`Ctrl+C` sai da rede limpo (avisa o relay e remove a regra de firewall).

## Se não funcionar

**A sala não aparece, mas `ping 25.0.0.2` responde.**
É firewall, 99% das vezes. O cliente já tenta marcar o adaptador como rede *Privada*, mas alguns
antivírus revertem. Confirme em `Configurações → Rede → VirtualLan → Perfil de rede → Privada`.
Depois, garanta que o jogo tem regra de entrada:
`netsh advfirewall firewall add rule name="Warcraft III" dir=in action=allow program="C:\...\war3.exe" enable=yes`

**Nem o ping funciona.**
Rode com `-v` nos dois lados e veja se o log mostra `Peer entrou`. Se não mostra, os dois não
estão na mesma rede — confira se `networkId` é idêntico nas duas máquinas (é impresso no início).
Nome e senha diferenciam maiúsculas na senha, mas não no nome.

**Sempre fica "via relay", nunca "caminho direto".**
Os dois lados estão atrás de NAT simétrico (comum em 4G/5G e em algumas operadoras de fibra).
Funciona, mas com a latência do VPS. Coloque o relay perto geograficamente.

**`Falha ao abrir \\.\Global\{...}.tap (execute como Administrador)`.**
É literalmente isso. O `app.manifest` pede elevação, mas se você rodou de um terminal já aberto
sem elevação, o UAC não aparece.

**`outro processo já está usando este adaptador`.**
O OpenVPN está usando o mesmo adaptador TAP. Crie um segundo:
`.\scripts\install-tap.ps1 -AdapterName VirtualLan2` e use `--adapter VirtualLan2`.

**Jogo antigo trava ou não lista salas.**
Alguns títulos de 2002 não gostam de MTU < 1500 em quadros grandes. O padrão é 1400 por
segurança; se o seu link não é PPPoE, tente 1460 editando `NetworkConfigurator.Mtu`.

## Segurança

Leia [`docs/SECURITY.md`](docs/SECURITY.md) antes de expor um relay. Resumo honesto:

- O tráfego é AES-256-GCM fim-a-fim; **o relay não consegue ler nem alterar nada**.
- **Todos os membros da rede compartilham a mesma chave** — não há isolamento entre eles.
- Entrar numa rede virtual expõe seus serviços de LAN (SMB, etc.) aos outros membros.
  Entre apenas em redes de gente que você conhece.
- Use uma senha longa. Ela é o único fator de autenticação.

## Desenvolvimento

```powershell
dotnet test                      # 35 testes: protocolo, cripto, switching, integração com relay
dotnet build VirtualLan.sln
.\scripts\build.ps1 -Relay       # publica os dois binários em dist/
```

Estrutura:

```
src/VirtualLan.Core     protocolo de fio, AES-GCM/HKDF, learning switch — sem I/O, 100% testável
src/VirtualLan.Relay    servidor UDP de rendezvous + relay
src/VirtualLan.Node     cliente Windows: P/Invoke do tap-windows6, hole punching, forwarding
tests/                  xunit; os testes de integração sobem um RelayServer real em loopback
```

Os testes de integração (`RelayIntegrationTests`) sobem um relay de verdade num socket de
loopback e falam com ele via UDP, cobrindo registro, atribuição de IP, divulgação de peers,
roteamento unicast/broadcast e isolamento entre redes distintas.

## Licença

Uso pessoal. `tap-windows6` é da OpenVPN Inc. e tem licença própria (GPL-2.0).
