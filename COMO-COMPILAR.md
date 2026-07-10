# Como compilar o VirtualLan a partir do código-fonte

Este guia é para quem **baixou o código** (clonou o repositório) e quer gerar o `VirtualLan.exe`
e o pacote pronto para compartilhar. O build é **autônomo**: se você não tiver o .NET, o próprio
script instala tudo. Ninguém precisa instalar o OpenVPN nem o .NET manualmente.

> Já tem o `VirtualLan.exe`? Então **este guia não é para você** — é só abrir o programa. Isto
> aqui é para **reconstruir** o executável a partir do código.

---

## O que você precisa

- **Windows 10 ou 11.**
- **Conexão com a internet** (só na primeira vez, para baixar as dependências).
- Nada mais. Nem .NET, nem OpenVPN, nem Visual Studio — o script cuida disso.

> Por que não preciso instalar o .NET? Porque o `scripts\package.ps1` chama o
> `scripts\ensure-dotnet.ps1`, que **instala o .NET SDK automaticamente numa pasta local
> (`.dotnet`)** caso ele não exista — sem privilégio de administrador e sem alterar o seu sistema.

---

## Passo a passo (do zero ao pacote pronto)

### 1. Baixe o código

Com o Git:

```powershell
git clone https://github.com/TeteuPower/LanHouse.git
cd LanHouse
```

Ou, sem Git: no GitHub, clique em **Code ▸ Download ZIP**, extraia, e abra a pasta.

### 2. Gere o executável e o pacote

Abra o **PowerShell** dentro da pasta do projeto e rode:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\package.ps1
```

> O `-ExecutionPolicy Bypass` é só para o Windows não bloquear o script baixado. Se você já
> permite scripts, pode rodar direto: `.\scripts\package.ps1`.

Na primeira vez, esse comando sozinho:

1. **Instala o .NET SDK** localmente, se você ainda não tiver (baixa ~200 MB, uma vez só).
2. **Roda os 35 testes** automatizados.
3. **Compila e publica** a interface como **um único `VirtualLan.exe` autocontido** (o runtime
   do .NET vai embutido — quem receber não instala .NET).
4. **Baixa e embute o driver TAP** da OpenVPN ao lado do executável (por isso o amigo não
   precisa de OpenVPN nem de internet para o driver).
5. **Copia** o `LEIA-ME.txt` e o `TUTORIAL.md`.
6. **Compacta** tudo em `dist\VirtualLan.zip`.

### 3. Pronto — o que você recebe

```
dist\
├── VirtualLan\               (a pasta para compartilhar)
│   ├── VirtualLan.exe        ← o programa
│   ├── tap-windows-...exe    ← o driver (instalado sozinho na 1ª execução)
│   ├── LEIA-ME.txt
│   └── TUTORIAL.md
├── VirtualLan.zip            ← ESTE é o arquivo que você manda para o amigo
└── extras\                   (avançado: relay dedicado Windows/Linux e o cliente de linha de comando)
```

Envie o **`dist\VirtualLan.zip`** para o seu amigo. Ele extrai e abre o `VirtualLan.exe`. Só isso.

---

## Rodar durante o desenvolvimento (opcional)

Se você só quer mexer no código e testar, sem gerar o pacote:

```powershell
# resolve/instala o SDK e guarda o caminho do dotnet
$dotnet = (& .\scripts\ensure-dotnet.ps1 | Select-Object -Last 1)

& $dotnet build VirtualLan.sln -c Release     # compila tudo
& $dotnet test                                # roda os 35 testes
& $dotnet run --project src\VirtualLan.App    # abre a interface direto do código
```

Se você já tem o .NET SDK 8+ instalado, pode usar `dotnet` diretamente, sem o `ensure-dotnet`.

---

## Perguntas comuns

**Por que a pasta `dist\` não está no repositório?**
Porque ela é **resultado de compilação**, não código-fonte. O executável (68 MB) e o driver são
**gerados** a partir do código — guardá-los no Git incharia o repositório e seria redundante. A
regra é: *fonte no Git, artefato reconstruído pelo script*. Por isso `bin/`, `obj/`, `dist/` e
`.dotnet/` estão no `.gitignore`.

**Preciso rodar como Administrador para compilar?**
Não. Compilar e empacotar **não** exige admin. Só o uso do `VirtualLan.exe` (abrir o adaptador de
rede) pede elevação — e ele mesmo pede sozinho quando você o executa.

**O `package.ps1` falhou ao baixar o driver TAP.**
Ele avisa e continua (o app baixa o driver em tempo de execução na primeira conexão). Se quiser o
pacote 100% offline, rode de novo com internet, ou coloque um `tap-windows*.exe` dentro de
`dist\VirtualLan\` manualmente.

**Onde fica o .NET que o script instalou?**
Numa pasta `.dotnet` dentro do projeto. É local, não afeta o resto do seu Windows, e pode ser
apagada à vontade (o script reinstala se precisar).

---

Dúvidas sobre **usar** o programa (não compilar) estão no [`docs/TUTORIAL.md`](docs/TUTORIAL.md).
