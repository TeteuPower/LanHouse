# VirtualLan — Arquitetura

## 1. Problema

Jogos de LAN clássicos (Warcraft III, Age of Empires, CS 1.6, Diablo II) descobrem partidas
enviando **broadcast UDP** para `255.255.255.255` ou para o broadcast da sub-rede. Broadcast é
um conceito de **Camada 2 (Ethernet)** e não é roteado pela internet.

Portanto, um túnel IP comum (Camada 3) **não resolve**: o cliente nunca "vê" a sala criada pelo host.

## 2. Solução

Construir um **switch Ethernet virtual distribuído**:

```
   PC A (host)                                          PC B (cliente)
┌──────────────────┐                                ┌──────────────────┐
│  Warcraft III    │                                │  Warcraft III    │
│       ↓ UDP bcast│                                │  ↑ UDP bcast     │
│  ┌────────────┐  │                                │  ┌────────────┐  │
│  │ TAP adapter│  │  ← adaptador Ethernet virtual →│  │ TAP adapter│  │
│  │ 25.0.0.1/24│  │                                │  │ 25.0.0.2/24│  │
│  └─────┬──────┘  │                                │  └─────▲──────┘  │
│        │ raw eth frames                           │        │         │
│  ┌─────▼──────┐  │                                │  ┌─────┴──────┐  │
│  │VirtualLan  │  │  ==== UDP direto (P2P) ====>   │  │VirtualLan  │  │
│  │  .Node     │  │      (após hole punching)      │  │  .Node     │  │
│  └─────┬──────┘  │                                │  └─────▲──────┘  │
└────────┼─────────┘                                └────────┼─────────┘
         │                                                   │
         │            ┌───────────────────────┐              │
         └───────────►│  VirtualLan.Relay     │◄─────────────┘
            fallback  │  (VPS, UDP :7777)     │  fallback
            + rendezvous└─────────────────────┘
```

O `.Node` lê quadros Ethernet crus do adaptador TAP, criptografa, e entrega ao peer.
O peer escreve o quadro no seu próprio TAP. O jogo enxerga um switch físico.

## 3. Componentes

| Projeto | TFM | Papel |
|---|---|---|
| `VirtualLan.Core` | `net8.0` | Protocolo de fio, criptografia, switching L2. Sem I/O. |
| `VirtualLan.Relay` | `net8.0` | Servidor de rendezvous + relay de dados. Roda em qualquer VPS Linux. |
| `VirtualLan.Node` | `net8.0-windows` | Cliente. Driver TAP + NAT traversal + forwarding. |
| `VirtualLan.Core.Tests` | `net8.0` | Testes de round-trip do protocolo e da cripto. |

## 4. Modelo de confiança

O relay é **semi-confiável**: ele roteia pacotes mas **nunca vê o tráfego em claro**.

A partir de `(networkName, password)` derivamos com HKDF-SHA256:

```
networkId = HKDF(ikm=password, salt=networkName, info="vlan/v1/network-id", 16 bytes)
dataKey   = HKDF(ikm=password, salt=networkName, info="vlan/v1/data-key",   32 bytes)
```

- O relay só conhece `networkId` (usado para agrupar sessões). Não consegue derivar `dataKey`.
- O payload é sempre AES-256-GCM com `dataKey`.

**Limitação assumida:** todos os peers da rede compartilham a mesma chave. Não há isolamento
criptográfico *entre* membros da rede. Isso é aceitável para o caso de uso (um grupo de amigos),
e é exatamente o modelo do Hamachi/Radmin. Está documentado em `SECURITY.md`.

## 5. Protocolo de fio

Todo pacote começa com um cabeçalho de 8 bytes, em claro:

```
offset  size  campo
0       4     magic = "VLAN" (0x56 0x4C 0x41 0x4E)
4       1     version = 1
5       1     type
6       1     flags (reservado, 0)
7       1     reserved (0)
```

### Tipos

| Code | Nome | Direção | Corpo |
|---|---|---|---|
| 0x01 | `Register` | node → relay | `networkId(16) │ nodeId(16) │ mac(6) │ nLocal(1) │ [ip(4) port(2)]*` |
| 0x02 | `RegisterAck` | relay → node | `assignedIndex(1) │ nPeers(1) │ PeerRecord*` |
| 0x03 | `PeerUpdate` | relay → node | `nPeers(1) │ PeerRecord*` |
| 0x04 | `Keepalive` | node → relay | `networkId(16) │ nodeId(16)` |
| 0x05 | `Punch` | node → node | `nodeId(16) │ nonce(4)` |
| 0x06 | `PunchAck` | node → node | `nodeId(16) │ nonce(4)` |
| 0x10 | `DataDirect` | node → node | `srcNodeId(16) │ nonce(12) │ ciphertext │ tag(16)` |
| 0x11 | `DataRelay` | node → relay → node | `networkId(16) │ srcNodeId(16) │ dstNodeId(16) │ nonce(12) │ ciphertext │ tag(16)` |
| 0x20 | `Disconnect` | node → relay | `networkId(16) │ nodeId(16)` |
| 0x7F | `Error` | relay → node | `code(1) │ utf8Message` |

`PeerRecord` (35 bytes): `nodeId(16) │ mac(6) │ index(1) │ publicIp(4) │ publicPort(2) │ localIp(4) │ localPort(2)`

Em `DataRelay`, `dstNodeId` todo-zero significa **broadcast para a rede**. O relay lê apenas
`networkId` e `dstNodeId` — ambos fora do texto cifrado, mas **dentro do AAD**, portanto não
podem ser adulterados sem invalidar a tag GCM.

### AAD (Additional Authenticated Data)

- `DataDirect`: `header(8) ‖ srcNodeId(16)`
- `DataRelay` : `header(8) ‖ networkId(16) ‖ srcNodeId(16) ‖ dstNodeId(16)`

### Nonce (12 bytes) — evitando reuso

```
nonce = nodeId[0..4]  ‖  counter(8, big-endian)
```

`nodeId` é **gerado aleatoriamente a cada execução** (não é persistido). Consequências:

- Prefixo distinto por nó → dois peers nunca colidem no espaço de nonce.
- Prefixo distinto por *sessão* → um restart do processo não reusa nonces com a mesma chave,
  que seria uma falha catastrófica em AES-GCM.

O nó não precisa persistir identidade nenhuma: o **MAC virtual vem do próprio driver TAP**
(`TAP_IOCTL_GET_MAC`), que já é estável entre reinícios. Isso também garante que o MAC que
anunciamos aos peers é exatamente o que a pilha do Windows aceita como destino ao receber
um quadro — se inventássemos um MAC, todo unicast seria descartado pelo adaptador.

## 6. Switching (Camada 2)

O `.Node` implementa um **learning switch** idêntico ao de um switch físico:

**Quadro vindo do TAP (jogo → rede):**
1. Lê o MAC de destino (bytes 0..6 do quadro).
2. Se for broadcast (`ff:ff:ff:ff:ff:ff`) ou multicast (bit 0 do 1º byte = 1) → **flood** para todos os peers.
3. Senão, consulta a `MacTable`. Se conhecido → unicast para aquele peer. Se desconhecido → flood.

**Quadro vindo de um peer (rede → jogo):**
1. Aprende `srcMac → peer` na `MacTable` (TTL 5 min).
2. Escreve o quadro cru no TAP.

É esse flood de broadcast que faz a sala do host aparecer na lista LAN do jogo.

## 7. NAT traversal

Máquina de estados por peer:

```
        ┌──────────┐  peer descoberto  ┌───────────┐  PunchAck recebido  ┌────────┐
        │ Unknown  │ ────────────────► │ Punching  │ ──────────────────► │ Direct │
        └──────────┘                   └─────┬─────┘                     └───┬────┘
                                             │ 3 s sem sucesso               │ 10 s sem tráfego
                                             ▼                               │
                                       ┌───────────┐ ◄─────────────────────-─┘
                                       │  Relayed  │
                                       └───────────┘
                                        (segue tentando punch a cada 5 s)
```

1. Ambos os nós registram no relay via UDP. O relay observa o **endpoint público** (`srcIp:srcPort`
   do datagrama), que é o mapeamento criado pelo NAT.
2. O relay envia a cada nó o `PeerRecord` do outro (endpoint público **e** local).
3. Cada nó dispara `Punch` simultaneamente para o endpoint público e o local do peer.
   Isso abre a "furo" no NAT de ambos os lados — o pacote de saída de A cria o mapeamento que
   permite a entrada do pacote de B.
4. Ao receber `Punch`, responde `PunchAck` para a origem. Ao receber `PunchAck`, fixa aquele
   endpoint como caminho direto.
5. Enquanto não houver caminho direto, os dados vão por `DataRelay`. A troca é transparente
   e sem perda de pacotes.
6. `Punch` também serve de **keepalive** no caminho direto (a cada 15 s), mantendo vivo o
   mapeamento NAT.

**Tenta o endpoint local também** para o caso dos dois estarem na mesma LAN física
(hairpin NAT frequentemente falha; o caminho local sempre funciona).

**Limitação conhecida:** NAT simétrico dos dois lados torna hole punching UDP impossível sem
port prediction. Nesse caso a conexão permanece em `Relayed` — funciona, só com mais latência.

## 8. Endereçamento

O relay atribui um `index` de 1 a 254 por rede. O nó configura o TAP como:

```
IP      25.0.0.<index>
máscara 255.255.255.0
MTU     1400
```

O bloco `25.0.0.0/8` era usado pelo Hamachi (é do UK MoD, não roteável na internet pública em
qualquer cenário doméstico realista). ARP entre os nós resolve normalmente porque broadcast
funciona.

**MTU 1400:** overhead total = 28 (IP+UDP) + 8 (header) + 16 (srcNodeId) + 12 (nonce) + 16 (tag)
= 80 bytes. `1400 + 80 = 1480 < 1500`, com folga para PPPoE.

## 9. Threading e I/O

- Um `Task` de leitura do TAP (`FileStream` overlapped, `isAsync: true`).
- Um `Task` de leitura do socket UDP (`ReceiveFromAsync`).
- Um `Task` de manutenção (punch, keepalive, expiração de MAC, re-registro).
- Buffers vêm de `ArrayPool<byte>.Shared` para não pressionar o GC — jitter de GC vira lag no jogo.
- Sem locks no caminho quente: a `MacTable` e o dicionário de peers são `ConcurrentDictionary`.
