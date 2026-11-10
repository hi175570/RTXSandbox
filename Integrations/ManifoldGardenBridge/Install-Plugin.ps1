[CmdletBinding()]
param(
    [string]$GameRoot = 'C:\D\games\Manifold Garden',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$integrationRoot = $PSScriptRoot
$project = Join-Path $integrationRoot 'Plugin\RTXSandbox.ManifoldGarden.Plugin.csproj'
$pluginDirectory = Join-Path $GameRoot 'BepInEx\plugins\RTXSandbox.ManifoldGardenBridge'

if (-not (Test-Path -LiteralPath (Join-Path $GameRoot 'ManifoldGarden.exe'))) {
    throw "Manifold Garden was not found at: $GameRoot"
}
if (-not (Test-Path -LiteralPath (Join-Path $GameRoot 'BepInEx\core\BepInEx.IL2CPP.dll'))) {
    throw 'The compatible BepInEx IL2CPP pre.1 loader is not installed.'
}
if (Get-Process -Name 'ManifoldGarden' -ErrorAction SilentlyContinue) {
    throw 'Close Manifold Garden before replacing the bridge plugin.'
}

dotnet build $project -c $Configuration -p:ManifoldGardenRoot="$GameRoot"
if ($LASTEXITCODE -ne 0) {
    throw "Plugin build failed with exit code $LASTEXITCODE"
}

$source = Join-Path $integrationRoot "Plugin\bin\$Configuration\netstandard2.1\RTXSandbox.ManifoldGarden.Plugin.dll"
New-Item -ItemType Directory -Path $pluginDirectory -Force | Out-Null
Copy-Item -LiteralPath $source -Destination $pluginDirectory -Force
Write-Host "Installed bridge plugin: $(Join-Path $pluginDirectory 'RTXSandbox.ManifoldGarden.Plugin.dll')"
