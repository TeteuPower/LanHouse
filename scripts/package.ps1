<#
.SYNOPSIS
    Gera o pacote pronto para compartilhar: uma pasta (e um .zip) com o VirtualLan.exe
    autocontido, sem exigir que o amigo instale o .NET.

.DESCRIPTION
    Publica a GUI como executavel unico self-contained para win-x64 em dist\VirtualLan,
    junto do LEIA-ME e do tutorial, e compacta em dist\VirtualLan.zip. Tambem publica o
    relay standalone (Windows e Linux) e o cliente CLI em dist\extras para usos avancados.

    O amigo so precisa do conteudo de dist\VirtualLan (ou do .zip). Nada de instalar runtime.

.EXAMPLE
    .\scripts\package.ps1
    .\scripts\package.ps1 -SkipTests -NoZip
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [switch] $SkipTests,
    [switch] $NoZip
)

$ErrorActionPreference = 'Stop'
$root   = (Resolve-Path "$PSScriptRoot\..").Path
$dist   = Join-Path $root 'dist'
$app    = Join-Path $dist 'VirtualLan'
$extras = Join-Path $dist 'extras'

# Garante um .NET SDK 8+ (instala local em .dotnet se faltar). Torna o build autonomo.
$dotnet = (& "$PSScriptRoot\ensure-dotnet.ps1" -Channel '8.0' | Select-Object -Last 1)

Write-Host '==> Limpando dist' -ForegroundColor Cyan
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force $app | Out-Null
New-Item -ItemType Directory -Force $extras | Out-Null

if (-not $SkipTests) {
    Write-Host '==> Rodando os testes' -ForegroundColor Cyan
    $env:DOTNET_ROLL_FORWARD = 'Major'
    & $dotnet test "$root\tests\VirtualLan.Core.Tests\VirtualLan.Core.Tests.csproj" -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Testes falharam.' }
}

$common = @(
    '-c', $Configuration,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '--nologo'
)

Write-Host '==> Publicando a GUI (VirtualLan.exe, win-x64, autocontido)' -ForegroundColor Cyan
& $dotnet publish "$root\src\VirtualLan.App\VirtualLan.App.csproj" -r win-x64 @common -o $app
if ($LASTEXITCODE -ne 0) { throw 'Publish da GUI falhou.' }

# Referenciar os projetos exe (Node/Relay) deixa .runtimeconfig.json e .pdb soltos no publish.
# O pacote de compartilhamento precisa de um unico arquivo: VirtualLan.exe.
Get-ChildItem $app -File | Where-Object { $_.Name -ne 'VirtualLan.exe' } | Remove-Item -Force

Write-Host '==> Copiando LEIA-ME e tutorial' -ForegroundColor Cyan
# Reescreve o LEIA-ME com BOM para acentos aparecerem certos em qualquer editor.
$leia = Get-Content "$root\deploy\LEIA-ME.txt" -Raw -Encoding UTF8
Set-Content -Path (Join-Path $app 'LEIA-ME.txt') -Value $leia -Encoding UTF8
Copy-Item "$root\docs\TUTORIAL.md" (Join-Path $app 'TUTORIAL.md') -Force

Write-Host '==> Embutindo o driver TAP (pacote 100% offline para o amigo)' -ForegroundColor Cyan
# O app procura um 'tap-windows*.exe' ao lado do executavel e o usa em vez de baixar. Com isso,
# quem receber o zip nao precisa de internet para o driver nem de instalar o OpenVPN.
# Nao vai para o repositorio (GPL-2.0): baixamos aqui, na hora de empacotar (dist e ignorado).
$driverUrl = 'https://build.openvpn.net/downloads/releases/tap-windows-9.24.7-I601-Win10.exe'
$driverDst = Join-Path $app 'tap-windows-9.24.7-I601-Win10.exe'
try {
    Invoke-WebRequest -Uri $driverUrl -OutFile $driverDst -UseBasicParsing
    Write-Host "    driver embutido: $([math]::Round((Get-Item $driverDst).Length/1KB,0)) KB"
}
catch {
    Write-Warning "Nao consegui baixar o driver TAP para embutir (o app baixara em runtime): $($_.Exception.Message)"
}

Write-Host '==> Publicando extras (relay Windows/Linux e CLI)' -ForegroundColor Cyan
& $dotnet publish "$root\src\VirtualLan.Relay\VirtualLan.Relay.csproj" -r win-x64   @common -o (Join-Path $extras 'relay-win-x64')
if ($LASTEXITCODE -ne 0) { throw 'Publish do relay (win) falhou.' }
& $dotnet publish "$root\src\VirtualLan.Relay\VirtualLan.Relay.csproj" -r linux-x64 @common -o (Join-Path $extras 'relay-linux-x64')
if ($LASTEXITCODE -ne 0) { throw 'Publish do relay (linux) falhou.' }
& $dotnet publish "$root\src\VirtualLan.Node\VirtualLan.Node.csproj"   -r win-x64   @common -o (Join-Path $extras 'cli-win-x64')
if ($LASTEXITCODE -ne 0) { throw 'Publish do CLI falhou.' }

Get-ChildItem $extras -Filter *.pdb -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force

if (-not $NoZip) {
    Write-Host '==> Compactando dist\VirtualLan.zip' -ForegroundColor Cyan
    $zip = Join-Path $dist 'VirtualLan.zip'
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path $app -DestinationPath $zip
}

Write-Host ''
Write-Host 'PRONTO.' -ForegroundColor Green
Write-Host "Pasta pronta para compartilhar: $app"
if (-not $NoZip) { Write-Host "Zip pronto para enviar:         $(Join-Path $dist 'VirtualLan.zip')" }
Write-Host ''
Get-ChildItem $app | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize
