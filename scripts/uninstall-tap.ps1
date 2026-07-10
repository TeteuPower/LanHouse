#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Remove o adaptador virtual do VirtualLan e a regra de firewall que o cliente cria.

.DESCRIPTION
    Não desinstala o driver tap-windows6 em si: ele pode estar em uso pelo OpenVPN.
    Para removê-lo por completo, use Adicionar/Remover Programas → "TAP-Windows".
#>
[CmdletBinding()]
param(
    [string] $AdapterName = 'VirtualLan'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$FirewallRule = 'VirtualLan (rede virtual 25.0.0.0/24)'

Write-Host '==> Removendo regra de firewall...' -ForegroundColor Cyan
netsh advfirewall firewall delete rule name="$FirewallRule" | Out-Null

$tapctl = "$env:ProgramFiles\OpenVPN\bin\tapctl.exe"

if (Test-Path $tapctl) {
    Write-Host "==> Removendo adaptador '$AdapterName' via tapctl..." -ForegroundColor Cyan
    & $tapctl delete $AdapterName
}
else {
    Write-Warning "tapctl.exe não encontrado. Remova o adaptador '$AdapterName' pelo Gerenciador de Dispositivos."
}

Write-Host 'Concluído.' -ForegroundColor Green
