# Prompt para continuar o VirtualLan (cole no Claude do VS Code)

> Copie tudo abaixo da linha e cole como sua primeira mensagem para o agente no VS Code.
> Ele está com o repositório aberto em `C:\Trabalho\lanhouse`.

---

Você é um desenvolvedor full stack Sênior assumindo um projeto em andamento. O repositório já
está aberto em `C:\Trabalho\lanhouse`. **Não comece a codar antes de ler os documentos abaixo,
na ordem.**

## Leia primeiro (obrigatório, nesta ordem)
1. `docs\HANDOFF.md` — estado atual, o que falta, plano detalhado, armadilhas já mapeadas.
2. `docs\PRODUTO.md` — o que o usuário espera como produto final (modelo/visão).
3. `docs\ARCHITECTURE.md` — por que é um switch L2 distribuído, protocolo de fio, NAT traversal.
4. `docs\SECURITY.md` — modelo de confiança (relay não lê o tráfego; NodeId efêmero; etc.).
5. `README.md` e `docs\TUTORIAL.md` — como se instala e usa hoje.
Depois, olhe o código: `src\VirtualLan.Core`, `src\VirtualLan.Relay`, `src\VirtualLan.Node`,
`tests\VirtualLan.Core.Tests`.

## Contexto do produto (resumo — detalhes em PRODUTO.md)
Recriar o Garena/Hamachi de forma caseira e self-hosted: dois PCs **Windows** em redes/NATs
diferentes se enxergam como se estivessem na mesma LAN, para jogar **qualquer** jogo em modo
LAN. O usuário é não técnico no momento de usar. O produto final é: **um app com interface
gráfica**, que **instala o driver sozinho** na primeira vez, e que ele possa **mandar como um
arquivo/instalador** para o amigo. Sem terminal, sem PowerShell manual. Windows dos dois lados.

## Estado atual (detalhes em HANDOFF.md)
Protótipo funcional e testado: relay + cliente CLI + protocolo + cripto (AES-256-GCM/HKDF) +
learning switch + hole punching com fallback via relay. **35 testes passando**, build Release
limpo. Já corrigi o bug de detecção do adaptador TAP (ComponentId `root\tap0901`).

## O que falta (execute na ordem — é a task list de HANDOFF.md §5)
1. **Abstrair o motor** (`IFrameDevice`/`INetworkConfigurator`) e mover `NodeService`→`NodeEngine`
   para `VirtualLan.Core`, para poder testar em Linux. O produto segue Windows-only.
2. **Laboratório de NAT** (HANDOFF.md §6): dois nós reais em namespaces de rede Linux, atrás de
   NATs, com um mini-jogo que faz broadcast — prove a sala cruzando NAT (direto) e via relay
   (NAT simétrico). Técnica de namespaces já validada; comandos exatos estão no HANDOFF.
3. **Instalador do TAP embutido em C#** (sem exigir PowerShell do usuário): detectar ausência,
   baixar o instalador oficial da OpenVPN em runtime, instalar silencioso, criar+renomear o
   adaptador. Porte a lógica de `scripts\install-tap.ps1`.
4. **Interface gráfica (WinForms)**: relay/rede/senha, Conectar/Desconectar, lista de peers com
   estado (direto/relay), log; primeira execução dispara o instalador do TAP com progresso.
5. **Empacotar**: `.exe` único self-contained + pasta `dist\VirtualLan\` com LEIA-ME, pronta
   para zipar; opcionalmente um instalador Inno Setup.
6. **Verificação final**: suite completa + build Release + o teste de NAT rodando de verdade +
   revisão do código do TAP por subagente. Só considere pronto com dois nós trocando broadcast.

## Modo de trabalho — TRABALHE EM LOOP ATÉ TERMINAR
- Use a task list (TaskCreate/TaskUpdate) e siga item a item, marcando in_progress/completed.
- **Não pare para pedir confirmação** entre etapas. Tome as decisões razoáveis de um Sr dev,
  registre-as e siga. Só pare se algo for genuinamente ambíguo quanto ao produto (raro — o
  PRODUTO.md cobre) ou exigir credencial/segredo do usuário.
- **Depois de cada etapa, verifique rodando**: `dotnet build -c Release` (0 warnings, é
  TreatWarningsAsErrors) e `dotnet test`. Não avance com o vermelho.
- Ao terminar todas as etapas, **releia a task list e a "Definição de pronto" (HANDOFF.md §9);
  se algum item não estiver satisfeito, volte e continue**. Repita até tudo verde e o
  empacotável gerado.
- No fim, entregue: o caminho do `.exe`/zip/instalador finais, o resultado dos testes, e um
  resumo curto do que mudou. Deixe claro o único ponto que não dá para provar fora do Windows
  real (o ioctl do driver TAP) e como você validou todo o resto (NAT lab).

## Armadilhas já mapeadas (não repita — detalhes em HANDOFF.md §7)
- Se um `.cs` longo aparecer **truncado no meio de uma string** para o compilador mas certo no
  editor: reescreva o arquivo inteiro de uma vez (dessincronização já observada). Confirme com
  build.
- `.ps1` deve ser salvo em **UTF-8 com BOM** (senão os acentos viram lixo no PowerShell).
- Não desative o tratamento de `SIO_UDP_CONNRESET`; não persista o `NodeId`; MTU 1400 é
  proposital; `AesGcm` não é thread-safe (o `FrameCipher` já serializa).

Comece lendo os documentos e, em seguida, retomando a etapa 1. Trabalhe até o produto final
estar pronto e empacotado.
