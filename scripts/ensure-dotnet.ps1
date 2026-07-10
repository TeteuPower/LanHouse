<#
.SYNOPSIS
    Garante um .NET SDK 8+ disponivel e devolve o caminho do 'dotnet' a usar.

.DESCRIPTION
    Se ja existe um dotnet 8+ no PATH, usa esse. Caso contrario, baixa e instala o SDK
    LOCALMENTE em .dotnet (sem admin, sem alterar o sistema), usando o instalador oficial da
    Microsoft, e devolve o caminho do dotnet local.

    Apenas a ultima linha da saida e o caminho do dotnet; as mensagens informativas vao para o
    host (Write-Host) e nao poluem o valor retornado. Assim o package.ps1 pode fazer:
        $dotnet = (& "$PSScriptRoot\ensure-dotnet.ps1" | Select-Object -Last 1)

.EXAMPLE
    $dotnet = (& .\scripts\ensure-dotnet.ps1 | Select-Object -Last 1)
    & $dotnet --version
#>
[CmdletBinding()]
param([string] $Channel = '8.0')

$ErrorActionPreference = 'Stop'

function Test-DotnetOk([string] $exe) {
    if (-not $exe) { return $false }
    try {
        $v = & $exe --version 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $v) { return $false }
        return ([int]($v.ToString().Split('.')[0]) -ge 8)
    }
    catch { return $false }
}

# 1) dotnet ja instalado no PATH?
$cmd = Get-Command dotnet -ErrorAction SilentlyContinue
if ($cmd -and (Test-DotnetOk $cmd.Source)) {
    Write-Host "==> Usando o .NET SDK do sistema ($(& $cmd.Source --version))" -ForegroundColor DarkGray
    return $cmd.Source
}

# 2) instalacao local previa (de uma execucao anterior)?
$root     = (Resolve-Path "$PSScriptRoot\..").Path
$localDir = Join-Path $root '.dotnet'
$localExe = Join-Path $localDir 'dotnet.exe'
if (Test-DotnetOk $localExe) {
    Write-Host "==> Usando o .NET SDK local em .dotnet" -ForegroundColor DarkGray
    return $localExe
}

# 3) instala o SDK localmente, sem admin, sem tocar no sistema
Write-Host "==> .NET SDK $Channel+ nao encontrado. Baixando e instalando localmente em .dotnet (sem admin)..." -ForegroundColor Cyan
Write-Host "    (isso baixa ~200 MB uma unica vez; precisa de internet)" -ForegroundColor DarkGray

$installer = Join-Path $env:TEMP 'dotnet-install.ps1'
Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing

# Roda o instalador num PowerShell com politica liberada para nao esbarrar em ExecutionPolicy.
& powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Channel $Channel -InstallDir $localDir -NoPath | Out-Null

if (-not (Test-DotnetOk $localExe)) { throw "Falha ao instalar o .NET SDK $Channel em '$localDir'." }

Write-Host "==> .NET SDK instalado em .dotnet ($(& $localExe --version))" -ForegroundColor Green
return $localExe
