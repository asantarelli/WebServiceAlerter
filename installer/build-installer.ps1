<#
.SYNOPSIS
    Construye el instalador MSI de WebServiceAlerter.

.DESCRIPTION
    Publica el servicio y el Viewer, y empaqueta ambos en un único MSI que registra el servicio,
    crea los accesos directos y deja todo andando.

    El paquete NUNCA incluye appsettings.Local.json: ahí viven las credenciales durante el
    desarrollo. La contraseña de la casilla de envío no viaja en el instalador por diseño — el
    blob DPAPI está atado a cada máquina y se genera después de instalar, con:

        "C:\Program Files\WebServiceAlerter\WebServiceAlerter.exe" --protect-password

.EXAMPLE
    .\installer\build-installer.ps1
#>
[CmdletBinding()]
param(
    [string] $Version = '0.2.0',
    [string] $Runtime = 'win-x64',
    [string] $Output
)

$ErrorActionPreference = 'Stop'

$root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }
$repo = Split-Path -Parent $root

if (-not $Output) { $Output = Join-Path $root 'out' }
$Output = [System.IO.Path]::GetFullPath($Output)

$publishService = Join-Path $Output 'stage-service'
$publishViewer = Join-Path $Output 'stage-viewer'
$msi = Join-Path $Output "WebServiceAlerter-$Version-$Runtime.msi"

# WiX se usa como herramienta LOCAL del repo, fijada a la versión 5. WiX 7 exige aceptar el EULA
# del Open Source Maintenance Fee, que para uso comercial implica pagar; la 5 es libre y entiende
# el mismo esquema que usa Product.wxs. Como herramienta local, clonar el repo y correr
# `dotnet tool restore` alcanza para construir el instalador.
Push-Location $repo
try {
    dotnet tool restore | Out-Null
    dotnet wix --version | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "No pude usar WiX. Probá:  dotnet tool restore"
    }
}
finally {
    Pop-Location
}

Write-Host "Publicando el servicio..." -ForegroundColor Cyan
dotnet publish (Join-Path $repo 'src\WebServiceAlerter\WebServiceAlerter.csproj') `
    --configuration Release --runtime $Runtime --self-contained true `
    --output $publishService -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "Falló la publicación del servicio." }

Write-Host "Publicando el Viewer..." -ForegroundColor Cyan
dotnet publish (Join-Path $repo 'src\WebServiceAlerter.Viewer\WebServiceAlerter.Viewer.csproj') `
    --configuration Release --runtime $Runtime --self-contained true `
    --output $publishViewer -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "Falló la publicación del Viewer." }

# Nunca empaquetar la configuración de desarrollo: tiene la casilla real y el blob DPAPI.
$local = Join-Path $publishService 'appsettings.Local.json'
if (Test-Path $local) {
    Remove-Item $local -Force
    Write-Host "Quitado appsettings.Local.json del paquete." -ForegroundColor Yellow
}

# Verificación explícita en vez de confiar en el paso anterior: este paquete se distribuye, y un
# descuido acá publica las credenciales de un dominio propio en el equipo de cada cliente.
$sospechosos = Get-ChildItem $publishService, $publishViewer -Recurse -File -Include '*.json' |
    Where-Object { (Get-Content $_.FullName -Raw) -match 'AQAAANCMnd8|ProtectedPassword"\s*:\s*"[A-Za-z0-9+/]{20,}' }

if ($sospechosos) {
    throw "El paquete contiene credenciales: $($sospechosos.FullName -join ', ')"
}

Write-Host "Construyendo el MSI..." -ForegroundColor Cyan

# Desde la carpeta del instalador: WixUILicenseRtf resuelve License.rtf relativo al directorio
# actual, y el manifiesto de herramientas locales se encuentra igual porque dotnet lo busca
# hacia arriba en el árbol.
Set-Location $root
dotnet wix build 'Product.wxs' `
    -d ProductVersion=$Version `
    -d PublishDir=$publishService `
    -d ViewerPublishDir=$publishViewer `
    -ext WixToolset.UI.wixext `
    -ext WixToolset.Util.wixext `
    -arch x64 `
    -out $msi

if ($LASTEXITCODE -ne 0) { throw "Falló la construcción del MSI." }

$size = [math]::Round((Get-Item $msi).Length / 1MB, 1)

Write-Host ""
Write-Host "Listo. $size MB" -ForegroundColor Green
Write-Host "  $msi" -ForegroundColor Green
Write-Host ""
Write-Host "Instalación:    msiexec /i `"$msi`"" -ForegroundColor Gray
Write-Host "Silenciosa:     msiexec /i `"$msi`" /qn" -ForegroundColor Gray
Write-Host "Desinstalar:    msiexec /x `"$msi`"" -ForegroundColor Gray
Write-Host ""
Write-Host "Después de instalar, una vez por equipo, en consola elevada:" -ForegroundColor Yellow
Write-Host "  `"C:\Program Files\WebServiceAlerter\WebServiceAlerter.exe`" --configure-smtp" -ForegroundColor Yellow
Write-Host "" -ForegroundColor Yellow
Write-Host "Sin ese paso la instalación queda muda: el instalador se publica abierto y por eso" -ForegroundColor Yellow
Write-Host "no lleva los datos de la casilla adentro." -ForegroundColor Yellow
