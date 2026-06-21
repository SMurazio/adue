param(
    [Parameter(Mandatory = $true)]
    [string]$ServerDll,
    [Parameter(Mandatory = $true)]
    [string]$Root,
    [string]$LogPath = '',
    [string]$ErrorLogPath = ''
)

$ErrorActionPreference = 'Stop'

$Host.UI.RawUI.WindowTitle = 'MMO Server'
Set-Location -LiteralPath $Root

if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
    $env:MMO_SERVER_LOG_FILE = $LogPath
}

if (-not [string]::IsNullOrWhiteSpace($ErrorLogPath)) {
    $env:MMO_SERVER_ERR_LOG_FILE = $ErrorLogPath
}

$localDotnet = Join-Path $Root '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { 'dotnet' }
& $dotnet $ServerDll
