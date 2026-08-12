<#
.SYNOPSIS
    Registra WebServiceAlerter como servicio de Windows. Requiere consola elevada.

.DESCRIPTION
    Alta del servicio, arranque automático y política de reinicio ante fallas. Esto es lo mismo
    que después hará el instalador; tenerlo como script permite probar el modo de ejecución real
    sin empaquetar nada.

    El servicio corre como LocalSystem, que es lo que necesita para descifrar el blob DPAPI de la
    contraseña SMTP (scope LocalMachine) y para escribir en ProgramData.

.EXAMPLE
    .\scripts\install-service.ps1
#>
[CmdletBinding()]
param(
    [string] $Path,
    [string] $Name = 'WebServiceAlerter',
    [string] $DisplayName = 'WebServiceAlerter - Monitor de webservices'
)

$ErrorActionPreference = 'Stop'

# Ver la nota en publish.ps1: $PSScriptRoot no sirve dentro de param en Windows PowerShell 5.1.
$root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }

if (-not $Path) { $Path = Join-Path $root '..\publish' }

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    throw "Hace falta una consola de PowerShell ejecutada como administrador."
}

$exe = [System.IO.Path]::GetFullPath((Join-Path $Path 'WebServiceAlerter.exe'))

if (-not (Test-Path $exe)) {
    throw "No encontré $exe. Corré primero .\scripts\publish.ps1"
}

$existing = Get-Service -Name $Name -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "El servicio ya existe; lo doy de baja para recrearlo." -ForegroundColor Yellow
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $Name -Force
        $existing.WaitForStatus('Stopped', '00:00:30')
    }
    sc.exe delete $Name | Out-Null
    Start-Sleep -Milliseconds 800
}

Write-Host "Registrando el servicio..." -ForegroundColor Cyan

New-Service -Name $Name `
            -BinaryPathName "`"$exe`"" `
            -DisplayName $DisplayName `
            -Description 'Monitorea disponibilidad y latencia de webservices (ARCA y otros) y avisa cuando se caen y cuando se recuperan.' `
            -StartupType Automatic | Out-Null

# Ante una caída del proceso, reintentar en vez de quedarse mudo: un monitor que se muere en
# silencio es peor que no tener monitor, porque el usuario cree que lo está cuidando.
sc.exe failure $Name reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

Start-Service -Name $Name
Start-Sleep -Seconds 2

$service = Get-Service -Name $Name

Write-Host ""
Write-Host "Servicio '$Name': $($service.Status)" -ForegroundColor Green
Write-Host ""
Write-Host "Para seguirlo:" -ForegroundColor Gray
Write-Host "  Get-Content `"$([Environment]::GetFolderPath('CommonApplicationData'))\WebServiceAlerter\status.json`"" -ForegroundColor Gray
Write-Host "  Get-EventLog -LogName Application -Source $Name -Newest 20" -ForegroundColor Gray
