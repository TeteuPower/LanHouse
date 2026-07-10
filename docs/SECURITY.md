# VirtualLan — Modelo de segurança

Escrito para ser lido antes de expor um relay na internet. Nenhuma afirmação aqui é otimista
por conveniência.

## O que o sistema garante

**Confidencialidade e integridade fim-a-fim do tráfego.**
Todo quadro Ethernet trafega dentro de AES-256-GCM. A chave (`dataKey`) é derivada por HKDF-SHA256
de `(nome da rede, senha)` e nunca sai das máquinas dos jogadores.

**O relay não consegue ler nem alterar o tráfego.**
Ele só recebe `networkId = HKDF(senha, salt=nome, info="vlan/v1/network-id")`. Como HKDF-Expand
é uma PRF, conhecer a saída para um `info` não ajuda a recuperar a saída de outro `info` nem a
senha. Os campos que o relay precisa ler para rotear (`networkId`, `srcNodeId`, `dstNodeId`)
ficam em claro mas **dentro do AAD** do GCM: alterá-los invalida a tag no destino.

**Nonces nunca se repetem sob a mesma chave.**
`nonce = nodeId[0..4] ‖ contador(8)`, com `nodeId` aleatório de 128 bits regerado a cada
execução do processo. Reuso de nonce em GCM revela o XOR dos textos claros e permite forjar
tags — por isso o `NodeId` é deliberadamente efêmero e não persistido.

**Anti-amplificação no relay.**
O relay só encaminha `DataRelay` de um nó já registrado *e* apenas se o datagrama chegou do
endpoint que aquele nó registrou. Um atacante não consegue usá-lo como refletor DDoS.

## O que o sistema NÃO garante

**Não há isolamento criptográfico entre membros da mesma rede.**
Todos compartilham a mesma `dataKey`. Qualquer participante consegue decifrar o tráfego de
qualquer outro. Isto é intencional (é o modelo do Hamachi e do Radmin VPN) e adequado a um
grupo de amigos. **Não use para nada além de jogos.** Se você precisa de isolamento par-a-par,
o caminho é um handshake tipo Noise IK por par de peers, com chaves estáticas por nó.

**A senha é o único fator.**
Quem souber `(nome, senha)` entra na rede e lê tudo. Use uma senha longa. Não há PBKDF caro
(Argon2/scrypt) na derivação: o `networkId` é enviado ao relay, então um relay hostil poderia
montar um ataque de dicionário offline contra ele. Para redes com senha fraca isso é real.
Mitigação: senha de alta entropia (frase de 5+ palavras aleatórias).

**Punch/PunchAck não são autenticados criptograficamente.**
Um atacante que consiga ver ou adivinhar o `nodeId` de um peer e o nonce de 32 bits pode
forjar um `PunchAck` e fazer você mandar tráfego (cifrado) para o endpoint dele. Isso é um
**blackhole/DoS**, não um vazamento — ele não tem a chave. O caminho se recupera sozinho em
10 s (timeout de ociosidade) e o tráfego volta ao relay.

**O relay vê metadados.**
Endereços IP públicos, tamanho e horário dos pacotes, quem fala com quem, quantos nós há em
cada `networkId`. Ele não sabe o nome da rede.

**Sem proteção contra replay no plano de dados.**
Um atacante on-path pode reinjetar um `DataDirect` capturado. GCM aceita (nonce válido, tag
válida) e o quadro é entregue de novo à pilha. Para jogos isso é ruído; para qualquer outro
uso, seria necessária uma janela deslizante de nonces recebidos por peer.

**A rede virtual é uma LAN de verdade.**
Ao entrar, você expõe à `25.0.0.0/24` todos os serviços que a sua máquina expõe à LAN:
compartilhamentos SMB, servidores de desenvolvimento, impressoras. Entre apenas em redes de
pessoas em quem você confia. O cliente marca o adaptador como rede **Privada** para que os
jogos funcionem — isso relaxa o Firewall do Windows para essa sub-rede.

## Superfície de ataque do relay

O relay é um processo sem privilégio que faz `bind` numa porta UDP alta e nunca toca no disco.
Todo pacote é entrada não-confiável: o parsing é `Try*`-based, sem exceções no caminho quente,
e um pacote malformado é descartado em silêncio. O `deploy/vlan-relay.service` roda com
`NoNewPrivileges`, `ProtectSystem=strict` e `RestrictAddressFamilies`.

Limites em vigor: 254 nós por rede; 8 endpoints locais anunciados por nó; datagramas acima de
1600 bytes descartados antes do parse; sessões expiram em 45 s sem keepalive.

## Como reportar um problema

Este é um projeto pessoal, não um produto. Se encontrar uma falha, abra uma issue descrevendo
o impacto concreto. Não há programa de bug bounty.
