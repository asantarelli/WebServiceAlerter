<#
.SYNOPSIS
    Compila WebServiceAlerter en modo release y lo deja listo para instalar.

.DESCRIPTION
    Publica autocontenido (self-contained): el paquete incluye el runtime de .NET, así que en el
    equipo del cliente no hay que instalar nada previo. Pesa más, pero evita la clase de soporte
    telefónico que arranca con "me dice que falta un componente".

    Deliberadamente SIN trimming ni single-file: la configuración se enlaza por reflexión y el
    recorte de ensamblados la rompe de formas que no aparecen hasta que el cliente cambia un
    valor. No vale la pena el ahorro de espacio.

.EXAMPLE
    .\scripts\publish.ps1
#>
[CmdletBinding()]
param(
    [string] $Output,
    [string] $Runtime = 'win-x64',
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot no está poblado dentro del bloque param en Windows PowerShell 5.1, así que la
# ruta se resuelve acá y con respaldo, para que el script ande igual en 5.1 y en pwsh 7.
$root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }

if (-not $Output) { $Output = Join-Path $root '..\publish' }

$project = Join-Path $root '..\src\WebServiceAlerter\WebServiceAlerter.csproj'
$Output = [System.IO.Path]::GetFullPath($Output)

Write-Host "Publicando $Configuration / $Runtime -> $Output" -ForegroundColor Cyan

if (Test-Path $Output) {
    Remove-Item $Output -Recurse -Force
}

dotnet publish $project `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --output $Output `
    -p:DebugType=none

if ($LASTEXITCODE -ne 0) {
    throw "Falló la publicación (código $LASTEXITCODE)."
}

# appsettings.Local.json es configuración de desarrollo y puede tener credenciales reales.
# Nunca debe viajar dentro de un paquete que se distribuye.
$local = Join-Path $Output 'appsettings.Local.json'
if (Test-Path $local) {
    Remove-Item $local -Force
    Write-Host "Quitado appsettings.Local.json del paquete (es local, no se distribuye)." -ForegroundColor Yellow
}

$size = [math]::Round((Get-ChildItem $Output -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)

Write-Host ""
Write-Host "Listo. $size MB en $Output" -ForegroundColor Green
Write-Host "Siguiente paso (consola elevada):  .\scripts\install-service.ps1" -ForegroundColor Gray
