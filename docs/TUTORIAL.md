# VirtualLan — Tutorial

Rede LAN virtual para jogar (ou trocar arquivos) com amigos em outra rede, em outra cidade,
atrás de outro roteador. O jogo enxerga um adaptador de rede comum, manda o broadcast de
descoberta de sala, e a sala aparece na lista — como se vocês estivessem na mesma casa.

**Você só precisa de um programa: `VirtualLan.exe`.** Ele tem interface gráfica, pede
administrador sozinho e, na primeira vez, instala o adaptador de rede virtual automaticamente.

> Com pressa? Vá direto para o **Início rápido**. Ele resolve o caso mais comum (você hospeda,
> o amigo entra) em poucos minutos, sem VPS e sem terminal.

---

## Início rápido

Combinem entre vocês **um nome de rede** e **uma senha** (qualquer coisa, iguais nos dois PCs).

### Quem vai ser o servidor (só um de vocês)

1. Abra o **VirtualLan.exe** (aceite o pedido de administrador do Windows).
2. Marque **“Hospedar o relay neste PC (eu sou o servidor)”**.
3. Deixe a **porta** em `7777`.
4. Preencha **Nome da rede** e **Senha**.
5. Clique **Conectar**.
   - Na primeira vez, ele instala o adaptador virtual — aceite o aviso do Windows. Leva alguns
     segundos e só acontece uma vez.
6. Aparece o campo **“Envie ao amigo: SEU_IP:7777”**. Clique **Copiar** e mande esse endereço
   para o amigo, junto com o nome da rede e a senha.

### Quem vai entrar (o amigo)

1. Abra o **VirtualLan.exe**.
2. **Não** marque a opção de servidor.
3. Em **“Servidor do amigo (host:porta)”**, cole o endereço que o host mandou
   (ex.: `200.100.50.10:7777`).
4. Digite o **mesmo** nome de rede e a **mesma** senha.
5. Clique **Conectar**.

### Jogar

Quando os dois aparecerem como **Conectado** (bolinha verde), abram o jogo e escolham
**LAN / Rede local / Multiplayer local**. O host cria a sala; ela aparece para o amigo em
alguns segundos.

Pronto. É isso. O resto deste documento é só para quando algo não funciona ou para usos
avançados.

---

## O que a janela mostra

- **Barra de status** (embaixo): a bolinha fica cinza (desconectado), laranja (conectando) ou
  verde (conectado — mostra o seu IP virtual, algo como `25.0.0.1`).
- **Participantes**: cada amigo conectado, com o IP virtual, o MAC e o estado do caminho:
  - **Direto** = tráfego ponto-a-ponto (P2P), latência mínima. É o normal depois de 1–2 s.
  - **Via relay** = passando pelo servidor. Funciona, só com um pouco mais de latência.
  - **Conectando** = tentando abrir o caminho direto.
- **Registro**: o log do que está acontecendo. Marque **“Log detalhado”** se precisar diagnosticar.

---

## Trocar arquivos (não só jogos)

A rede virtual é uma LAN de verdade. Para acessar as pastas compartilhadas do outro PC, abra o
**Explorador de Arquivos** e digite o IP virtual dele:

```
\\25.0.0.2
```

O IP de cada participante aparece na lista **Participantes**. Isso vale para qualquer coisa que
funcione em LAN: servidores de mídia, impressoras, ferramentas de dev, etc.

> ⚠️ Entrar numa rede virtual expõe à `25.0.0.0/24` os serviços que a sua máquina já expõe à LAN
> (compartilhamentos, etc.). Entre apenas em redes de gente em quem você confia. Veja
> [`SECURITY.md`](SECURITY.md).

---

## Sobre “ser o servidor”

Alguém precisa ter um endereço público onde o outro se conecta. No modo **servidor**, o próprio
VirtualLan faz esse papel a partir do seu PC:

- sobe o servidor de encontro (**relay**) dentro do app;
- abre a porta no **Firewall do Windows** automaticamente;
- tenta abrir a porta no seu **roteador via UPnP** (sem você mexer em nada);
- descobre o seu **IP público** e mostra o endereço pronto para compartilhar.

Se o UPnP funcionar (a maioria dos roteadores domésticos), não há mais nada a fazer. Se o log
mostrar um aviso de UPnP, faça **uma vez** o encaminhamento de porta no roteador:

| Campo | Valor |
|---|---|
| Protocolo | **UDP** (não TCP) |
| Porta externa | 7777 |
| IP interno | o IP deste PC na sua rede (ex.: 192.168.0.15) |
| Porta interna | 7777 |

> Dica: para o endereço não mudar quando o modem reiniciar, reserve o IP deste PC no DHCP do
> roteador e/ou use um DDNS grátis (DuckDNS, No-IP) como endereço no lugar do IP.

O relay é leve: assim que o caminho direto (P2P) é estabelecido, o tráfego do jogo nem passa por
ele. Deixe a janela do host aberta enquanto jogam.

---

## Problemas comuns

### “Conectado” dos dois lados, mas a sala não aparece no jogo

Quase sempre é **firewall/antivírus** bloqueando o jogo (não o VirtualLan). O app já marca a
rede virtual como **Privada**, mas alguns antivírus (Kaspersky, ESET, Avast) revertem isso.

1. Confirme o perfil da rede:
   ```powershell
   Get-NetConnectionProfile -InterfaceAlias VirtualLan
   # NetworkCategory precisa ser Private
   ```
   Se estiver `Public`:
   ```powershell
   Set-NetConnectionProfile -InterfaceAlias VirtualLan -NetworkCategory Private
   ```
2. Garanta que o **jogo** tem regra de entrada no Firewall:
   ```powershell
   netsh advfirewall firewall add rule name="Meu Jogo" dir=in action=allow program="C:\Caminho\jogo.exe" enable=yes
   ```

### O amigo nunca aparece em “Participantes”

Vocês não estão na mesma rede virtual, ou ele não alcança o seu servidor.

- Confiram o **nome da rede** e a **senha** — precisam ser idênticos (a senha diferencia
  maiúsculas de minúsculas; o nome não).
- No host, veja se o log diz que a porta foi liberada. Se o UPnP falhou, faça o encaminhamento
  de porta UDP 7777 (seção “Sobre ser o servidor”).
- Teste rápido do endereço: peça para o amigo abrir o navegador em `http://SEU_IP:7777` — não vai
  “carregar” nada (é UDP), mas serve para você confirmar que ele digitou o IP certo.

### Fica sempre “Via relay”, nunca “Direto”

Os dois lados estão atrás de **NAT simétrico** (comum em 4G/5G e em algumas operadoras de fibra).
O caminho direto por hole punching não é possível nesse cenário. **Funciona mesmo assim**, só com
a latência do servidor somada. Se um dos lados trocar de rede (ex.: sair do 4G para o Wi-Fi), o
caminho direto costuma se estabelecer sozinho.

### “Execute como Administrador”

O `VirtualLan.exe` já pede elevação ao abrir. Se você desabilitou o UAC ou iniciou de um jeito
que pulou o pedido, feche e abra de novo clicando com o botão direito → **Executar como
administrador**.

### Há mais de um adaptador TAP (ex.: você usa OpenVPN)

Se outro programa (OpenVPN, outro emulador de LAN) já usa um adaptador TAP, escolha o do
VirtualLan no campo **Adaptador** da janela. O adaptador criado pelo VirtualLan se chama
`VirtualLan`.

---

## Avançado — relay dedicado num VPS (opcional)

O modo servidor da GUI resolve o caso normal. Você só precisa de um VPS se:

- quiser um servidor **sempre ligado**, independente do seu PC; **ou**
- os **dois** lados estiverem atrás de CGNAT (comum em 4G/5G e planos de fibra sem IP público),
  caso em que nenhum dos dois consegue receber conexão de entrada em casa.

O relay é um binário minúsculo, sem estado, que roda em qualquer VPS Linux (ou Windows). Ele já
vem publicado em `extras/` dentro do pacote:

- `extras/relay-linux-x64/vlan-relay` — para VPS Linux
- `extras/relay-win-x64/vlan-relay.exe` — para um PC/Servidor Windows

### Subir no Linux

```bash
sudo install -m 0755 vlan-relay /usr/local/bin/vlan-relay
sudo useradd --system --no-create-home --shell /usr/sbin/nologin vlan
# unit systemd pronta no repositório:
sudo cp deploy/vlan-relay.service /etc/systemd/system/
sudo systemctl enable --now vlan-relay
sudo ufw allow 7777/udp
```

> Firewall da nuvem: além do `ufw`, libere UDP 7777 no painel do provedor (Security Group na AWS,
> Security List na Oracle, Firewall rules no GCP, NSG no Azure). É o que mais trava gente.

Depois, na GUI, **os dois** deixam a opção de servidor **desmarcada** e usam
`SEU_VPS:7777` no campo do servidor.

### Oracle Cloud (ARM, grátis)

O free tier da Oracle é ARM. Publique para ARM:

```powershell
dotnet publish .\src\VirtualLan.Relay\VirtualLan.Relay.csproj -c Release `
    -r linux-arm64 --self-contained true -p:PublishSingleFile=true -o .\dist\relay-linux-arm64
```

---

## Linha de comando (opcional)

Quem preferir terminal pode usar o cliente `vlan.exe` (em `extras/cli-win-x64`), com os mesmos
conceitos:

```
vlan --relay <host:porta> --network <nome> --password <senha> [opções]

  --adapter, -a <nome>   Adaptador TAP, se houver mais de um.
  --list-adapters        Lista os adaptadores e sai.
  --install-tap          Instala o driver/adaptador TAP e sai (sem PowerShell).
  --verbose, -v          Log de depuração.
```

---

## Desinstalar

- Para de usar: feche o VirtualLan. Ele remove a regra de firewall e sai da rede limpo.
- Para remover o adaptador virtual e o driver: **Adicionar ou Remover Programas → TAP-Windows**
  (só remova se você não usa OpenVPN, que compartilha o mesmo driver). Alternativamente, o
  script `scripts\uninstall-tap.ps1` remove o adaptador `VirtualLan`.

Detalhes honestos do modelo de segurança em [`SECURITY.md`](SECURITY.md).
