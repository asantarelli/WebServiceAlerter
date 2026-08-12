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
    [string] $Configuration = 'Release',

    # Sólo para probar el servicio en el equipo propio: incluye appsettings.Local.json en el
    # paquete. NUNCA usar para generar algo que se le entrega a un cliente.
    [switch] $IncludeLocalSettings
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot no está poblado dentro del bloque param en Windows PowerShell 5.1, así que la
# ruta se resuelve acá y con respaldo, para que el script ande igual en 5.1 y en pwsh 7.
$root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }

if (-not $Output) { $Output = Join-Path $root '..\publish' }

$project = Join-Path $root '..\src\WebServiceAlerter\WebServiceAlerter.csproj'
$viewerProject = Join-Path $root '..\src\WebServiceAlerter.Viewer\WebServiceAlerter.Viewer.csproj'
$Output = [System.IO.Path]::GetFullPath($Output)
$viewerOutput = Join-Path $Output 'Viewer'

Write-Host "Publicando $Configuration / $Runtime -> $Output" -ForegroundColor Cyan

# Borrar la carpeta anterior falla seguido con "acceso denegado" sobre alguna DLL nativa: el
# antivirus las escanea apenas se escriben y las retiene unos segundos. No es motivo para abortar
# una publicación, así que se reintenta y, si igual no se puede, se sigue: dotnet publish
# sobrescribe lo que haya.
if (Test-Path $Output) {
    $borrado = $false

    for ($intento = 1; $intento -le 4 -and -not $borrado; $intento++) {
        try {
            Remove-Item $Output -Recurse -Force -ErrorAction Stop
            $borrado = $true
        }
        catch {
            if ($intento -lt 4) {
                Write-Host "  carpeta anterior en uso, reintento $intento de 3..." -ForegroundColor DarkGray
                Start-Sleep -Seconds 2
            }
        }
    }

    if (-not $borrado) {
        Write-Host "  No pude limpiar la carpeta anterior; se sobrescribe encima." -ForegroundColor Yellow
    }
}

dotnet publish $project `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --output $Output `
    -p:DebugType=none

if ($LASTEXITCODE -ne 0) {
    throw "Falló la publicación del servicio (código $LASTEXITCODE)."
}

# El Viewer va en una subcarpeta del mismo paquete. Sin él sólo se instalaría el monitor, y la
# pantalla es lo que el cliente realmente usa: entregar uno sin el otro no es una instalación.
Write-Host "Publicando el Viewer -> $viewerOutput" -ForegroundColor Cyan

dotnet publish $viewerProject `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --output $viewerOutput `
    -p:DebugType=none

if ($LASTEXITCODE -ne 0) {
    throw "Falló la publicación del Viewer (código $LASTEXITCODE)."
}

# appsettings.Local.json es configuración de desarrollo y puede tener credenciales reales.
# Nunca debe viajar dentro de un paquete que se distribuye.
$local = Join-Path $Output 'appsettings.Local.json'
if (Test-Path $local) {
    if ($IncludeLocalSettings) {
        Write-Host ""
        Write-Host "  ATENCIÓN: el paquete INCLUYE appsettings.Local.json con tus credenciales." -ForegroundColor Red
        Write-Host "  Sirve para probar el servicio acá. No se lo entregues a nadie." -ForegroundColor Red
        Write-Host ""
    }
    else {
        Remove-Item $local -Force
        Write-Host "Quitado appsettings.Local.json del paquete (es local, no se distribuye)." -ForegroundColor Yellow
    }
}

$size = [math]::Round((Get-ChildItem $Output -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)

Write-Host ""
Write-Host "Listo. $size MB en $Output" -ForegroundColor Green
Write-Host "  servicio: WebServiceAlerter.exe" -ForegroundColor Gray
Write-Host "  pantalla: Viewer\WebServiceAlerterViewer.exe" -ForegroundColor Gray
Write-Host ""
Write-Host "Siguiente paso (consola elevada):  .\scripts\install-service.ps1" -ForegroundColor Gray
Write-Host ""
Write-Host "OJO: si dejaste el monitor corriendo en una consola, paralo antes de arrancar el" -ForegroundColor Yellow
Write-Host "servicio. Dos instancias miden en paralelo sobre la misma base y mandan las alertas" -ForegroundColor Yellow
Write-Host "por duplicado." -ForegroundColor Yellow
