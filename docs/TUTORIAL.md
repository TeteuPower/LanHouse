# VirtualLan — Tutorial do zero até a sala aparecer

Feito para você e um amigo. Tempo estimado: 30–40 min na primeira vez, ~10 s nas próximas.

Ordem: **(0) decidir onde fica o relay → (1) compilar → (2) testar em casa → (3) subir o relay → (4) jogar.**

O passo 2 é o mais importante e quase todo mundo pula. Ele valida 90% do sistema sem você
gastar um centavo nem mexer em roteador. Se algo estiver errado, você descobre ali — não às
23h de sexta com seu amigo esperando.

---

## Passo 0 — Onde vai rodar o relay?

O relay é um processo minúsculo com um único trabalho: apresentar você e seu amigo um ao outro
(*rendezvous*) e, se o NAT de vocês for hostil, encaminhar o tráfego. Ele precisa de um **IP
público alcançável**. Só isso.

Você tem duas opções, e a escolha depende de uma coisa: **se a sua operadora usa CGNAT.**

### Descubra se você está atrás de CGNAT (2 minutos)

CGNAT é quando a operadora te dá um IP privado e compartilha um IP público entre centenas de
clientes. Se for o seu caso, **abrir porta no roteador não funciona** — não há porta para abrir.

```powershell
# 1) Qual IP o mundo enxerga?
(Invoke-WebRequest -Uri "https://api.ipify.org" -UseBasicParsing).Content
```

Anote. Agora entre no painel do seu roteador (geralmente `http://192.168.0.1` ou
`http://192.168.1.1`) e procure **"IP WAN"**, "Status da Internet" ou "Endereço IP externo".

| Situação | Diagnóstico |
|---|---|
| O IP WAN do roteador é **igual** ao que o ipify mostrou | Sem CGNAT. Port forward funciona. |
| O IP WAN é **diferente** do ipify | Você está atrás de CGNAT. |
| O IP WAN começa com `100.64.` a `100.127.` | CGNAT, definitivamente. |
| O IP WAN começa com `10.`, `192.168.` ou `172.16–31.` | CGNAT (ou modem em modo roteador duplo). |

> No Brasil, CGNAT é a regra em planos residenciais de fibra e praticamente universal em
> 4G/5G. Se você não tem IP fixo contratado, assuma que está atrás de CGNAT até provar o
> contrário.

### Escolha

**Se você NÃO está atrás de CGNAT** → pode rodar o relay no seu próprio PC. Grátis, mas seu PC
precisa estar ligado e você precisa abrir a porta no roteador. Vá para o **Passo 3-B**.

**Se você ESTÁ atrás de CGNAT** (ou não quer depender do seu PC) → precisa de um VPS. Vá para
o **Passo 3-A**. Não é caro:

| Provedor | Custo | Observação |
|---|---|---|
| **Oracle Cloud Always Free** | R$ 0 | 4 vCPU ARM + 24 GB RAM, para sempre. Cadastro exige cartão (não cobra). Use `-r linux-arm64` no build. |
| **Hetzner CX22** | ~€ 4/mês | Alemanha. Latência ~200 ms para o Brasil — ruim para jogo de reflexo. |
| **Contabo / Hostinger / Vultr** (SP ou Miami) | R$ 20–35/mês | São Paulo é o ideal: ~10–30 ms. |

> **Latência importa pouco na maior parte do tempo.** Assim que o hole punching funciona (1–2 s
> após conectar), o tráfego vira P2P direto e o relay some do caminho. A latência do VPS só
> pesa se vocês *ficarem* presos no fallback. Ainda assim, prefira um VPS no Brasil.

**Se você não sabe / quer testar antes de gastar** → faça o **Passo 2** primeiro. Ele funciona
sem VPS nenhum.

---

## Passo 1 — Compilar (uma vez, na sua máquina)

Você já tem o .NET SDK. Confirme que é a versão 8 ou superior:

```powershell
dotnet --version    # precisa ser 8.x ou 9.x
```

No PowerShell, dentro de `C:\Trabalho\lanhouse`:

```powershell
# Roda os 35 testes e publica o cliente Windows como .exe único
.\scripts\build.ps1
```

Resultado: `dist\win-x64\vlan.exe` — um executável autocontido, ~15 MB, sem dependência de
runtime. É esse arquivo que você manda para o seu amigo.

Para gerar também o binário do relay (Linux):

```powershell
.\scripts\build.ps1 -Relay          # gera dist\linux-x64\vlan-relay
```

> **Oracle Cloud (ARM):** o `build.ps1 -Relay` publica para `linux-x64`. Se o seu VPS for ARM
> (o free tier da Oracle é), use:
> ```powershell
> dotnet publish .\src\VirtualLan.Relay\VirtualLan.Relay.csproj -c Release `
>     -r linux-arm64 --self-contained true -p:PublishSingleFile=true -o .\dist\linux-arm64
> ```

Se você vai rodar o relay no Windows (Passo 3-B), não precisa publicar nada extra —
`dotnet run` serve.

---

## Passo 2 — Testar em casa, sem VPS e sem roteador

Aqui você prova que o driver TAP, a criptografia, o switch virtual e o jogo funcionam.
**Faça isto antes de qualquer outra coisa.** Precisa de dois PCs na mesma rede Wi-Fi/cabo.

Chame-os de **PC-A** (que também vai rodar o relay de teste) e **PC-B**.

### 2.1 — Instale o adaptador TAP nos dois PCs

Em cada máquina, abra o **PowerShell como Administrador** e rode:

```powershell
cd C:\Trabalho\lanhouse
.\scripts\install-tap.ps1
```

O script baixa o driver `tap-windows6` (assinado pela OpenVPN — não escrevemos driver próprio,
isso exigiria certificado EV e atestação da Microsoft) e cria um adaptador chamado `VirtualLan`.

O Windows vai pedir confirmação para instalar o driver. Aceite.

Confirme:

```powershell
.\dist\win-x64\vlan.exe --list-adapters
# 1 adaptador(es) TAP:
#   VirtualLan (tap0901, {A1B2C3D4-...})
```

### 2.2 — Suba o relay no PC-A

Descubra o IP local do PC-A:

```powershell
ipconfig | Select-String IPv4      # ex.: 192.168.0.15
```

Rode o relay (janela separada, deixe aberta):

```powershell
cd C:\Trabalho\lanhouse
dotnet run --project src\VirtualLan.Relay -- --port 7777 -v
# 21:04:02.110 [INF] Relay ouvindo em 0.0.0.0:7777/udp
```

Libere a porta no Firewall do Windows do PC-A (**PowerShell como Admin**):

```powershell
netsh advfirewall firewall add rule name="VirtualLan Relay" dir=in action=allow protocol=UDP localport=7777
```

### 2.3 — Conecte os dois PCs

**PC-A** (nova janela, PowerShell **como Administrador**):

```powershell
cd C:\Trabalho\lanhouse
.\dist\win-x64\vlan.exe --relay 192.168.0.15:7777 --network teste --password "cavalo bateria grampo correto"
```

**PC-B** (PowerShell **como Administrador**) — mesmo `--network`, mesma `--password`, apontando
para o IP do PC-A:

```powershell
.\vlan.exe --relay 192.168.0.15:7777 --network teste --password "cavalo bateria grampo correto"
```

### 2.4 — O que você deve ver

No PC-A:

```
21:04:11.220 [INF] Rede 'teste' → networkId 3f9a1c
21:04:11.221 [INF] Node 8c21f0a4 (efêmero)
21:04:11.244 [INF] TAP aberto: VirtualLan mac=00:ff:1a:2b:3c:4d driver=9.24
21:04:11.310 [INF] Configurando 'VirtualLan' → 25.0.0.1/24 mtu=1400
21:04:11.988 [INF] Registrado. Seu IP virtual é 25.0.0.1
21:04:23.101 [INF] Peer entrou: 25.0.0.2 [00:ff:aa:bb:cc:dd] 4e19b7c2
21:04:23.605 [INF] Caminho direto estabelecido com 25.0.0.2 via 192.168.0.22:51820
```

**Três coisas para conferir, nesta ordem:**

1. **`networkId` é idêntico nos dois PCs.** Se for diferente, o nome ou a senha não batem
   (a senha diferencia maiúsculas; o nome não). Nada mais vai funcionar até isso bater.
2. **`Peer entrou`** apareceu nos dois lados. Se não, o PC-B não alcança o relay — é firewall
   no PC-A ou IP errado.
3. **`Caminho direto estabelecido`.** Na mesma LAN isso é quase instantâneo, porque o
   VirtualLan tenta o endereço local antes do público (muito roteador doméstico não faz
   *hairpin NAT*, então o caminho local é o único confiável ali).

### 2.5 — Prove que a Camada 2 está viva

Do PC-A:

```powershell
ping 25.0.0.2
arp -a 25.0.0.2      # o MAC precisa aparecer: prova que ARP (broadcast!) atravessou o túnel
```

Se o `arp -a` mostra o MAC do PC-B, o switch virtual está funcionando. **É esse broadcast que
faz a sala do jogo aparecer.** Um túnel IP comum passaria no `ping` e falharia aqui.

### 2.6 — Abra o jogo

Nos dois PCs, abra o jogo e escolha **LAN / Rede local / Multiplayer local**.

- No **host**: crie a partida.
- No **cliente**: a sala aparece na lista em 1–5 segundos.

Se o `ping` funciona mas a sala não aparece, é firewall. Veja *Problemas comuns* no fim.

**Funcionou? Ótimo — o sistema está provado.** Agora é só trocar o relay local por um relay
com IP público, e vocês podem estar em cidades diferentes.

---

## Passo 3-A — Relay em um VPS (recomendado)

### Suba o binário

```powershell
# do seu Windows
scp .\dist\linux-x64\vlan-relay usuario@meu-vps.exemplo.com:~
```

### Instale como serviço

No VPS, via SSH:

```bash
# usuário sem privilégio: o relay não precisa de nenhum
sudo useradd --system --no-create-home --shell /usr/sbin/nologin vlan
sudo install -m 0755 ~/vlan-relay /usr/local/bin/vlan-relay

# unit já vem pronta no repositório, em deploy/vlan-relay.service
sudo curl -o /etc/systemd/system/vlan-relay.service \
  file:///caminho/para/deploy/vlan-relay.service     # ou simplesmente copie o arquivo

sudo systemctl daemon-reload
sudo systemctl enable --now vlan-relay
sudo systemctl status vlan-relay        # deve dizer "active (running)"
```

### Abra a porta — nos DOIS lugares

Esta é a pegadinha que mais trava gente em VPS de nuvem: existem **dois** firewalls.

```bash
# 1) firewall do sistema operacional
sudo ufw allow 7777/udp
```

```
# 2) firewall da nuvem (o que mais esquecem)
   AWS            → Security Group: Inbound rule, UDP 7777, source 0.0.0.0/0
   Oracle Cloud   → VCN → Security List: Ingress, UDP, porta 7777
   Google Cloud   → VPC → Firewall rules: allow udp:7777
   Azure          → NSG: Inbound, UDP 7777
   Hetzner/Vultr/DigitalOcean → em geral já vem aberto; confira o painel
```

> A Oracle Cloud, além da Security List, vem com `iptables` pré-configurado bloqueando tudo.
> Se `systemctl status` diz *running* e mesmo assim ninguém conecta, rode:
> `sudo iptables -I INPUT -p udp --dport 7777 -j ACCEPT` e persista com `netfilter-persistent save`.

### Verifique de fora

Do seu Windows:

```powershell
# Se o relay estiver vivo, ele responde com um pacote de Error (versão de protocolo inválida).
# Silêncio total = porta fechada em algum dos dois firewalls.
$c = New-Object System.Net.Sockets.UdpClient
$c.Client.ReceiveTimeout = 3000
$c.Connect("meu-vps.exemplo.com", 7777)
$c.Send([byte[]](0x56,0x4C,0x41,0x4E,99,1,0,0), 8) | Out-Null
try {
    $ep = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
    $r = $c.Receive([ref]$ep)
    "OK — relay respondeu $($r.Length) bytes"
} catch { "SEM RESPOSTA — verifique ufw E o firewall da nuvem" }
$c.Close()
```

Pule para o **Passo 4**.

---

## Passo 3-B — Relay no seu próprio PC (só sem CGNAT)

Só faça isto se o teste do Passo 0 confirmou que você **não** está atrás de CGNAT.

1. **Dê um IP fixo ao seu PC na LAN.** No roteador, em *DHCP → Reserva de endereço*, fixe o
   IP do seu PC (ex.: `192.168.0.15`). Se o IP mudar, o port forward quebra silenciosamente.

2. **Encaminhe a porta.** No roteador, em *Port Forwarding / Encaminhamento de portas / Virtual
   Server*:

   | Campo | Valor |
   |---|---|
   | Protocolo | **UDP** (não TCP) |
   | Porta externa | 7777 |
   | IP interno | 192.168.0.15 |
   | Porta interna | 7777 |

3. **Libere no Firewall do Windows** (PowerShell como Admin):

   ```powershell
   netsh advfirewall firewall add rule name="VirtualLan Relay" dir=in action=allow protocol=UDP localport=7777
   ```

4. **Rode o relay** e deixe rodando:

   ```powershell
   dotnet run --project src\VirtualLan.Relay -c Release -- --port 7777
   ```

   Se preferir um `.exe` solto (para colocar na inicialização do Windows):

   ```powershell
   dotnet publish .\src\VirtualLan.Relay\VirtualLan.Relay.csproj -c Release `
       -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\dist\relay-win-x64
   ```

5. **Seu endereço de relay** é o seu IP público: `(Invoke-WebRequest "https://api.ipify.org" -UseBasicParsing).Content`

> **Limitação real:** IP residencial muda. Se cair a luz ou o modem reiniciar, o endereço muda
> e vocês precisam reconectar com o IP novo. Um DDNS gratuito (DuckDNS, No-IP) resolve: você
> passa a usar `seunome.duckdns.org:7777` como `--relay`.

---

## Passo 4 — Jogar de verdade

### O que mandar para o seu amigo

Três coisas:

1. O arquivo `vlan.exe` (de `dist\win-x64\`).
2. O script `scripts\install-tap.ps1`.
3. O comando pronto — **exceto a senha**, que você manda por outro canal.

### Do lado do seu amigo (uma vez)

```powershell
# PowerShell como Administrador
.\install-tap.ps1
```

### Toda vez que forem jogar

**Os dois** rodam, em PowerShell **como Administrador**, com `--network` e `--password`
idênticos:

```powershell
.\vlan.exe --relay meu-vps.exemplo.com:7777 --network dota-sexta --password "cavalo bateria grampo correto"
```

Deixe a janela aberta. Abram o jogo, escolham **LAN**. O host cria a sala; ela aparece para o
cliente.

`Ctrl+C` sai limpo: avisa o relay e remove a regra de firewall que o cliente criou.

### Sobre a senha

É o **único** fator de autenticação. Quem souber `(nome, senha)` entra na rede e enxerga o
tráfego de todo mundo dentro dela. Use uma frase longa e aleatória — quatro ou cinco palavras
sem relação entre si. Não use `123456` "porque é só um jogo": a rede virtual é uma LAN de
verdade, e entrar nela expõe os compartilhamentos SMB e servidores locais da sua máquina aos
outros membros.

Detalhes honestos do modelo de segurança em [`SECURITY.md`](SECURITY.md).

---

## Referência rápida de comandos

```
vlan --relay <host:porta> --network <nome> --password <senha> [opções]

  --adapter, -a <nome>   Escolhe o adaptador TAP (se houver mais de um).
  --port <n>             Porta UDP local. Padrão: efêmera.
  --list-adapters        Lista os adaptadores TAP e sai.
  --verbose, -v          Log de depuração — use quando algo não funcionar.
  --trace                Log de trace (muito verboso).
```

A cada 30 s o cliente imprime um resumo:

```
21:09:41.002 [INF] — 1 peer(s), tx=48213 rx=51907 macs=1
21:09:41.002 [INF]    25.0.0.2  00:ff:aa:bb:cc:dd  4e19b7c2  direto 189.4.13.88:51820
```

`direto` = P2P, latência mínima. `via relay` = passando pelo VPS.

---

## Problemas comuns

### "O ping funciona, mas a sala não aparece no jogo"

É firewall. Praticamente sempre.

O cliente já tenta marcar o adaptador como rede **Privada**, mas alguns antivírus (Kaspersky,
ESET, Avast) revertem isso. Confirme:

```powershell
Get-NetConnectionProfile -InterfaceAlias VirtualLan
# NetworkCategory precisa ser: Private
```

Se estiver `Public`, force:

```powershell
Set-NetConnectionProfile -InterfaceAlias VirtualLan -NetworkCategory Private
```

Depois, garanta que o **jogo** tem regra de entrada:

```powershell
netsh advfirewall firewall add rule name="Warcraft III" dir=in action=allow `
    program="C:\Program Files\Warcraft III\war3.exe" enable=yes
```

### "Nem o ping funciona"

Rode com `-v` nos dois lados e procure a linha `Peer entrou`.

- **Não aparece `Peer entrou`** → vocês não estão na mesma rede virtual. Compare o `networkId`
  impresso no início nos dois PCs. Se for diferente: nome ou senha divergentes.
- **Aparece `Peer entrou` mas nada trafega** → o relay está recebendo mas não entregando.
  Verifique se o VPS não tem rate-limit de UDP.

### "Sempre fica `via relay`, nunca vira `direto`"

Os dois lados estão atrás de **NAT simétrico** — comum em 4G/5G e em algumas operadoras de
fibra. O hole punching UDP é impossível nesse cenário sem *port prediction*, que é frágil.

Funciona assim mesmo, só com a latência do VPS somada. Mitigação: um VPS geograficamente
próximo aos dois. Se um dos lados sair do NAT simétrico (ex.: sair do 4G para o Wi-Fi de
casa), o caminho direto se estabelece sozinho em ~1 s.

### `Falha ao abrir \\.\Global\{...}.tap (execute como Administrador)`

É literalmente isso. O `app.manifest` pede elevação via UAC, mas se você chamou o `.exe` de um
terminal já aberto sem elevação, o prompt não aparece. Abra o PowerShell com
*Executar como administrador*.

### `outro processo já está usando este adaptador`

O OpenVPN (ou outro cliente VPN) está segurando o mesmo adaptador TAP. Crie um segundo:

```powershell
.\scripts\install-tap.ps1 -AdapterName VirtualLan2
.\vlan.exe --adapter VirtualLan2 --relay ... --network ... --password ...
```

### `Há 2 adaptadores TAP. Escolha um com --adapter`

Exatamente o que diz. Rode `--list-adapters` e passe o nome.

### Jogo antigo trava, ou lista a sala e não conecta

Alguns títulos de ~2002 lidam mal com MTU abaixo de 1500 em quadros grandes. O padrão é 1400
por segurança (cobre PPPoE, comum em fibra residencial no Brasil). Se o seu link não é PPPoE,
edite `NetworkConfigurator.Mtu` para `1460` e recompile.

### A mesma sala aparece duas vezes na lista

Não deveria acontecer — o cliente evita duplicar quadros de broadcast quando parte dos peers
está direto e parte via relay. Se acontecer, é bug: rode com `--trace` e guarde o log.

---

## Desinstalar

```powershell
# PowerShell como Administrador
.\scripts\uninstall-tap.ps1
```

Remove o adaptador `VirtualLan` e a regra de firewall. **Não** remove o driver
`tap-windows6` — ele pode estar em uso pelo OpenVPN. Para removê-lo de vez:
*Adicionar ou Remover Programas → TAP-Windows*.

No VPS:

```bash
sudo systemctl disable --now vlan-relay
sudo rm /etc/systemd/system/vlan-relay.service /usr/local/bin/vlan-relay
sudo userdel vlan
```
