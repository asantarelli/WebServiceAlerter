<#
.SYNOPSIS
    Da de baja el servicio WebServiceAlerter. Requiere consola elevada.

.DESCRIPTION
    No toca la base de datos ni usersettings.json bajo ProgramData: dar de baja el servicio no es
    lo mismo que querer perder el historial. Para borrar esos datos, eliminá la carpeta a mano.

.EXAMPLE
    .\scripts\uninstall-service.ps1
#>
[CmdletBinding()]
param(
    [string] $Name = 'WebServiceAlerter'
)

$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    throw "Hace falta una consola de PowerShell ejecutada como administrador."
}

$service = Get-Service -Name $Name -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Host "El servicio '$Name' no está instalado." -ForegroundColor Yellow
    return
}

if ($service.Status -ne 'Stopped') {
    Write-Host "Deteniendo el servicio..." -ForegroundColor Cyan
    Stop-Service -Name $Name -Force
    $service.WaitForStatus('Stopped', '00:00:30')
}

sc.exe delete $Name | Out-Null

Write-Host "Servicio '$Name' dado de baja." -ForegroundColor Green
Write-Host "Los datos en ProgramData\WebServiceAlerter quedaron intactos." -ForegroundColor Gray
