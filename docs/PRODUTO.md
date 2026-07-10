# VirtualLan — Visão de Produto (o que o usuário espera)

## Problema de negócio

Jogos LAN clássicos (Warcraft III/DotA, Age of Empires, CS 1.6, Diablo II, Stronghold, etc.)
só enxergam partidas na mesma rede local. Amigos hoje moram em cidades diferentes, atrás de
NATs e operadoras diferentes. Existem soluções (Garena — que o usuário usava, Hamachi, Radmin
VPN, ZeroTier, GameRanger), mas são de terceiros, com cadastro, limites, anúncios, ou
descontinuadas. O usuário quer **a própria solução**, self-hosted, sem burocracia.

## Público-alvo

Ele e amigos — usuários **não técnicos** no momento de usar. Quem monta o relay (uma vez) pode
seguir um passo a passo; quem só joga não deve precisar de terminal nenhum.

## Definição de produto final (o "pronto")

Um aplicativo Windows com **interface gráfica** onde o usuário:
1. Digita: endereço do relay, nome da rede e senha (combinados entre os amigos).
2. Clica **Conectar**.
3. Na primeira vez, o app **instala sozinho** o adaptador de rede virtual (driver TAP), pedindo
   elevação quando necessário — sem PowerShell manual.
4. Abre o jogo, escolhe **LAN**, e a sala do amigo aparece na lista.

Distribuição: o usuário quer **mandar um arquivo** (um `.exe` único, uma pasta zipada, ou um
instalador) para o amigo. O amigo abre e usa. Nada de "rode estes 5 comandos".

## Requisitos que ele deixou explícitos

- **Windows dos dois lados** (a máquina dele e a do amigo). É o alvo único do produto.
- **Genérico**: qualquer jogo que use LAN, não só Warcraft. (A arquitetura L2 já garante isso.)
- **Sem burocracia**: sem cadastro, sem conta. A "autenticação" é só o par (nome da rede, senha).
- **Pronto e funcionando**: ele quer receber algo que roda, não um esqueleto.

## Não-objetivos (para não gastar esforço à toa)

- Não é multiplataforma de produção (Linux/Mac). Linux só aparece como ambiente de **teste**.
- Não é um serviço comercial com billing, contas, painel. É uma ferramenta self-hosted.
- Não precisa de isolamento criptográfico entre membros da mesma rede (todos confiam entre si).
  Ver `docs/SECURITY.md` — é o mesmo modelo do Hamachi, e é aceitável aqui.
- Não hospedar/distribuir o driver da OpenVPN no repo (licença). Baixar em runtime.

## Métrica de sucesso

Dois PCs em redes diferentes, cada um abrindo o app, digitando os mesmos dados, e conseguindo
ver e entrar na sala LAN um do outro dentro de ~1 minuto do primeiro uso — com o caminho
preferencialmente **direto** (P2P) e caindo para **relay** só quando o NAT for hostil.
