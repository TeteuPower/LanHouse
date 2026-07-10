<#
.SYNOPSIS
    Inicializa o git, faz o primeiro commit e envia para o GitHub.
.DESCRIPTION
    Rode UMA vez, no Windows, dentro de C:\Trabalho\lanhouse (PowerShell normal, nao precisa admin).
    Um .git parcial/corrompido pode ter sido deixado por um ambiente Linux; este script o remove
    e recria do zero a partir da arvore de trabalho, que esta completa e correta.
.NOTES
    O push pede sua autenticacao do GitHub (navegador ou token). E o unico passo que so voce pode
    fazer, porque envolve sua credencial.
#>
[CmdletBinding()]
param(
    [string] $RemoteUrl = 'https://github.com/TeteuPower/LanHouse.git',
    [string] $Branch    = 'main'
)

$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot\..

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw "git nao encontrado no PATH. Instale o Git para Windows (https://git-scm.com/download/win)."
}

Write-Host '==> Removendo .git anterior (se houver)...' -ForegroundColor Cyan
if (Test-Path .git) { Remove-Item -Recurse -Force .git }

Write-Host '==> git init + configuracao...' -ForegroundColor Cyan
git init | Out-Null
git branch -M $Branch
# CRLF: mantem final de linha de cada arquivo como esta; evita ruido no diff.
git config core.autocrlf false
# Se ainda nao tiver identidade global, define uma local para este repo:
if (-not (git config user.email)) { git config user.email 'inovacoes@trustsis.com' }
if (-not (git config user.name))  { git config user.name  'TeteuPower' }

Write-Host '==> Adicionando arquivos (obj/bin/dist ficam de fora pelo .gitignore)...' -ForegroundColor Cyan
git add -A

Write-Host '==> Commit...' -ForegroundColor Cyan
git commit -m "VirtualLan: prototipo funcional (relay + cliente + cripto), correcao do adaptador TAP, docs e handoff" | Out-Null

Write-Host '==> Configurando remote origin...' -ForegroundColor Cyan
if (git remote | Select-String -Quiet '^origin$') { git remote set-url origin $RemoteUrl }
else { git remote add origin $RemoteUrl }

Write-Host "==> Enviando para $RemoteUrl ($Branch)..." -ForegroundColor Cyan
Write-Host '    (o GitHub vai pedir seu login/token aqui)' -ForegroundColor DarkGray
git push -u origin $Branch

Write-Host ''
Write-Host 'Pronto. Repositorio publicado.' -ForegroundColor Green
git log --oneline -1
