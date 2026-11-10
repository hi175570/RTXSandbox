[CmdletBinding()]
param(
    [string]$GameRoot = 'C:\D\games\Manifold Garden',
    [string]$PackageUri = 'https://github.com/BepInEx/BepInEx/releases/download/v6.0.0-pre.1/BepInEx_UnityIL2CPP_x64_6.0.0-pre.1.zip',
    [bool]$DisableUnityLogListening = $true
)

$ErrorActionPreference = 'Stop'

$resolvedGameRoot = (Resolve-Path -LiteralPath $GameRoot).Path
$expectedRoot = (Resolve-Path -LiteralPath 'C:\D\games\Manifold Garden').Path
if ($resolvedGameRoot -ne $expectedRoot) {
    throw "Refusing to modify unexpected game directory: $resolvedGameRoot"
}

$gameExecutable = Join-Path $resolvedGameRoot 'ManifoldGarden.exe'
$metadata = Join-Path $resolvedGameRoot 'ManifoldGarden_Data\il2cpp_data\Metadata\global-metadata.dat'
if (-not (Test-Path -LiteralPath $gameExecutable) -or -not (Test-Path -LiteralPath $metadata)) {
    throw 'The selected directory is not the expected 64-bit IL2CPP Manifold Garden installation.'
}

$running = Get-Process -Name 'ManifoldGarden' -ErrorAction SilentlyContinue
if ($running) {
    foreach ($process in $running) {
        [void]$process.CloseMainWindow()
    }
    $running | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
    if (Get-Process -Name 'ManifoldGarden' -ErrorAction SilentlyContinue) {
        throw 'Manifold Garden is still running. Close it before installing the loader.'
    }
}

$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('rtx-manifold-bepinex-' + [Guid]::NewGuid().ToString('N'))
$packagePath = Join-Path $stagingRoot 'BepInEx-Unity.IL2CPP-win-x64.zip'
$expandedPath = Join-Path $stagingRoot 'expanded'
New-Item -ItemType Directory -Path $expandedPath -Force | Out-Null

Write-Host 'Downloading the official BepInEx Unity IL2CPP x64 build...'
Invoke-WebRequest -Uri $PackageUri -OutFile $packagePath
$packageHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
Expand-Archive -LiteralPath $packagePath -DestinationPath $expandedPath

$required = @('BepInEx', 'doorstop_config.ini', 'winhttp.dll')
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $expandedPath $name))) {
        throw "Downloaded package is missing required entry: $name"
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $expandedPath 'dotnet')) -and
    -not (Test-Path -LiteralPath (Join-Path $expandedPath 'mono'))) {
    throw 'Downloaded package contains neither the CoreCLR nor Mono runtime.'
}

# These are precisely the loader files created by this integration. Game-owned
# files are never selected here.
$loaderEntries = @(
    'BepInEx',
    'dotnet',
    'mono',
    'changelog.txt',
    'doorstop_config.ini',
    'winhttp.dll'
)
foreach ($name in $loaderEntries) {
    $target = Join-Path $resolvedGameRoot $name
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}

foreach ($entry in Get-ChildItem -LiteralPath $expandedPath -Force) {
    Copy-Item -LiteralPath $entry.FullName -Destination $resolvedGameRoot -Recurse -Force
}

if ($DisableUnityLogListening) {
    $configDirectory = Join-Path $resolvedGameRoot 'BepInEx\config'
    $configPath = Join-Path $configDirectory 'BepInEx.cfg'
    New-Item -ItemType Directory -Path $configDirectory -Force | Out-Null
    [System.IO.File]::WriteAllText($configPath, "[Logging]`r`nUnityLogListening = false`r`n")
}

$installedWinHttp = Join-Path $resolvedGameRoot 'winhttp.dll'
$installedHash = (Get-FileHash -LiteralPath $installedWinHttp -Algorithm SHA256).Hash
Write-Host "Package SHA-256: $packageHash"
Write-Host "Installed winhttp.dll SHA-256: $installedHash"
Write-Host "Installed BepInEx into: $resolvedGameRoot"
