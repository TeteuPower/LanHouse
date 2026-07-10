#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Instala o driver tap-windows6 e cria o adaptador virtual usado pelo VirtualLan.

.DESCRIPTION
    Não escrevemos nosso próprio driver de rede: um driver em modo kernel precisa de
    assinatura EV + atestação da Microsoft, o que custa caro e é lento. Reutilizamos o
    tap-windows6, que já vem assinado pela OpenVPN Inc. e é o mesmo driver que Hamachi-likes,
    OpenVPN e vários emuladores de LAN usam.

    O script é idempotente: rodar de novo não quebra nada.

.PARAMETER AdapterName
    Nome do adaptador a criar. Padrão: VirtualLan.

.EXAMPLE
    .\install-tap.ps1
    .\install-tap.ps1 -AdapterName VirtualLan2     # segundo adaptador, se o OpenVPN usa o primeiro
#>
[CmdletBinding()]
param(
    [string] $AdapterName = 'VirtualLan'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$NetClassKey  = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4D36E972-E325-11CE-BFC1-08002BE10318}'
$NetConnKey   = 'HKLM:\SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}'
$InstallerUrl = 'https://build.openvpn.net/downloads/releases/latest/tap-windows-latest-stable.exe'

# O mesmo driver aparece como 'tap0901' (instalador do OpenVPN) ou 'root\tap0901' (criado pelo
# tapctl, que usa o enumerador root). Normalizamos removendo o prefixo do enumerador.
$SupportedIds = @('tap0901', 'tap0801', 'tapoas')

function Get-NormalizedComponentId([string] $ComponentId) {
    ($ComponentId -split '\\')[-1]
}

function Get-TapAdapters {
    Get-ChildItem $NetClassKey -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match '^\d{4}$' } |
        ForEach-Object {
            $props = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
            if (-not $props) { return }
            if ($props.PSObject.Properties.Name -notcontains 'ComponentId') { return }
            if ($props.PSObject.Properties.Name -notcontains 'NetCfgInstanceId') { return }

            $normalized = Get-NormalizedComponentId $props.ComponentId
            if ($normalized -notin $SupportedIds) { return }

            $name = (Get-ItemProperty "$NetConnKey\$($props.NetCfgInstanceId)\Connection" -ErrorAction SilentlyContinue).Name

            [pscustomobject]@{
                Name        = $name
                ComponentId = $props.ComponentId
                InstanceId  = $props.NetCfgInstanceId
            }
        }
}

function Find-TapTool {
    # tapctl vem com o OpenVPN moderno; tapinstall (devcon) vem com o pacote tap-windows standalone.
    $candidates = @(
        "$env:ProgramFiles\OpenVPN\bin\tapctl.exe",
        "$env:ProgramFiles\TAP-Windows\bin\tapinstall.exe",
        "${env:ProgramFiles(x86)}\TAP-Windows\bin\tapinstall.exe"
    )
    $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

function Install-TapDriver {
    Write-Host '==> Driver TAP nao encontrado. Baixando o instalador oficial da OpenVPN...' -ForegroundColor Cyan

    $installer = Join-Path $env:TEMP 'tap-windows-installer.exe'

    try {
        Invoke-WebRequest -Uri $InstallerUrl -OutFile $installer -UseBasicParsing
    }
    catch {
        throw @"
Falha ao baixar o driver TAP de $InstallerUrl
$($_.Exception.Message)

Alternativa manual:
  1. Instale o OpenVPN (https://openvpn.net/community-downloads/) marcando o componente
     'TAP Virtual Ethernet Adapter'.
  2. Rode este script de novo.
"@
    }

    Write-Host '==> Instalando (silencioso). O Windows pode pedir confirmacao do driver.' -ForegroundColor Cyan
    $proc = Start-Process -FilePath $installer -ArgumentList '/S' -Wait -PassThru
    if ($proc.ExitCode -ne 0) { throw "Instalador retornou codigo $($proc.ExitCode)." }

    Remove-Item $installer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
}

function New-TapAdapter {
    $tool = Find-TapTool
    if (-not $tool) { throw 'Driver instalado, mas nem tapctl.exe nem tapinstall.exe foram encontrados.' }

    Write-Host "==> Criando adaptador com $(Split-Path $tool -Leaf)..." -ForegroundColor Cyan

    if ($tool -like '*tapctl.exe') {
        $output = & $tool create --name $AdapterName 2>&1
        $exit = $LASTEXITCODE
    }
    else {
        Push-Location (Split-Path $tool -Parent)
        try {
            $output = & $tool install OemVista.inf tap0901 2>&1
            $exit = $LASTEXITCODE
        }
        finally { Pop-Location }
    }

    $text = ($output | Out-String).Trim()

    # tapctl sai com codigo 1 quando o adaptador ja existe. Isso nao e um erro para nos:
    # o objetivo do script e "existir um adaptador chamado $AdapterName", nao "criar um".
    if ($exit -ne 0) {
        if ($text -match 'already exists') {
            Write-Host "    Adaptador '$AdapterName' ja existia. Seguindo." -ForegroundColor DarkGray
        }
        else {
            throw "Criacao do adaptador falhou (exit $exit).`n$text"
        }
    }

    Start-Sleep -Seconds 2
}

function Write-Diagnostics {
    Write-Host ''
    Write-Host 'Diagnostico — todos os adaptadores de rede no registro:' -ForegroundColor Yellow

    Get-ChildItem $NetClassKey -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match '^\d{4}$' } |
        ForEach-Object {
            $props = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
            if (-not $props -or $props.PSObject.Properties.Name -notcontains 'ComponentId') { return }

            $instanceId = if ($props.PSObject.Properties.Name -contains 'NetCfgInstanceId') { $props.NetCfgInstanceId } else { $null }
            $name = if ($instanceId) { (Get-ItemProperty "$NetConnKey\$instanceId\Connection" -ErrorAction SilentlyContinue).Name }

            [pscustomobject]@{
                Chave       = $_.PSChildName
                ComponentId = $props.ComponentId
                Compativel  = (Get-NormalizedComponentId $props.ComponentId) -in $SupportedIds
                Nome        = $name
            }
        } | Format-Table -AutoSize
}

# ---------------------------------------------------------------------------------- main

Write-Host 'VirtualLan — instalacao do adaptador virtual' -ForegroundColor Green

$existing = @(Get-TapAdapters)

if ($existing.Count -eq 0) {
    if (-not (Find-TapTool)) { Install-TapDriver }
    New-TapAdapter
    $existing = @(Get-TapAdapters)
}

if ($existing.Count -eq 0) {
    Write-Diagnostics
    throw @'
Nenhum adaptador TAP compativel apos a instalacao.

Se a tabela acima mostra um adaptador com ComponentId contendo 'tap0901' mas Compativel=False,
e um bug do script — reporte. Se nao mostra nenhum, abra o Gerenciador de Dispositivos e
verifique se ha um "TAP-Windows Adapter V9" em Adaptadores de rede.
'@
}

# Renomeia o primeiro adaptador que ainda nao tem o nome desejado.
$names = @($existing | ForEach-Object { $_.Name })

if ($names -notcontains $AdapterName) {
    $target = $existing | Select-Object -First 1
    Write-Host "==> Renomeando '$($target.Name)' -> '$AdapterName'" -ForegroundColor Cyan
    Rename-NetAdapter -Name $target.Name -NewName $AdapterName -ErrorAction Stop
}
else {
    Write-Host "==> Adaptador '$AdapterName' ja esta pronto." -ForegroundColor DarkGray
}

Write-Host ''
Write-Host 'Pronto. Adaptadores TAP disponiveis:' -ForegroundColor Green
Get-TapAdapters | Format-Table -AutoSize

Write-Host @"
Proximo passo:

  vlan.exe --relay SEU_SERVIDOR:7777 --network dota-sexta --password "uma senha forte" --adapter "$AdapterName"

Rode como Administrador. O mesmo --network e --password em todos os PCs.
"@ -ForegroundColor Yellow
