<#
.SYNOPSIS
    Compila e publica os dois binários: vlan.exe (cliente Windows) e vlan-relay (servidor).

.EXAMPLE
    .\build.ps1                 # cliente Windows
    .\build.ps1 -Relay          # cliente + relay para linux-x64 (para subir no VPS)
#>
[CmdletBinding()]
param(
    [switch] $Relay,
    [string] $Configuration = 'Release',
    [string] $OutputRoot = "$PSScriptRoot\..\dist"
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."

# Garante um .NET SDK 8+ (instala local em .dotnet se faltar).
$dotnet = (& "$PSScriptRoot\ensure-dotnet.ps1" -Channel '8.0' | Select-Object -Last 1)

Write-Host '==> Testes' -ForegroundColor Cyan
& $dotnet test "$root\tests\VirtualLan.Core.Tests\VirtualLan.Core.Tests.csproj" -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'Testes falharam.' }

Write-Host '==> Publicando cliente (win-x64, self-contained)' -ForegroundColor Cyan
& $dotnet publish "$root\src\VirtualLan.Node\VirtualLan.Node.csproj" `
    -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o "$OutputRoot\win-x64" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish do cliente falhou.' }

if ($Relay) {
    Write-Host '==> Publicando relay (linux-x64, self-contained)' -ForegroundColor Cyan
    & $dotnet publish "$root\src\VirtualLan.Relay\VirtualLan.Relay.csproj" `
        -c $Configuration -r linux-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -o "$OutputRoot\linux-x64" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish do relay falhou.' }
}

Write-Host ''
Write-Host "Binários em $OutputRoot" -ForegroundColor Green
Get-ChildItem $OutputRoot -Recurse -Filter 'vlan*' | Select-Object FullName, Length | Format-Table -AutoSize
