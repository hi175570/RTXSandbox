param(
    [string] $Configuration = 'Release',
    [string] $PackageRoot
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false # Expected failing processes are asserted below.
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $PackageRoot) { $PackageRoot = Join-Path $repoRoot "bin/$Configuration/net9.0" }
$PackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path
$exeName = if ($IsWindows) { 'RTXSandbox.exe' } else { 'RTXSandbox' }
$sourceExe = Join-Path $PackageRoot $exeName
if (-not (Test-Path -LiteralPath $sourceExe)) { throw "Build the game first: $sourceExe" }

foreach ($fixture in @('Patcher', 'Plugin')) {
    & dotnet build (Join-Path $repoRoot "Examples/PreloaderProbe/$fixture/$fixture.csproj") -c $Configuration -p:NuGetAudit=false --nologo
    if ($LASTEXITCODE -ne 0) { throw "Could not build $fixture fixture." }
}

$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = Join-Path $temporaryRoot ('rtxsandbox-preloader-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$originalDisable = $env:RTX_SANDBOX_DISABLE_BEPINEX
$originalFail = $env:RTX_SANDBOX_PROBE_FAIL
$originalOutput = $env:RTX_SANDBOX_OUTPUT_ROOT
$originalPlugins = $env:RTX_SANDBOX_PLUGIN_ROOT
try {
    $env:RTX_SANDBOX_DISABLE_BEPINEX = $null
    $env:RTX_SANDBOX_PROBE_FAIL = $null
    $env:RTX_SANDBOX_OUTPUT_ROOT = $testRoot
    $env:RTX_SANDBOX_PLUGIN_ROOT = $null
    Copy-Item -LiteralPath $sourceExe -Destination $testRoot
    Copy-Item -LiteralPath (Join-Path $PackageRoot 'Managed') -Destination $testRoot -Recurse
    New-Item -ItemType Directory -Path (Join-Path $testRoot 'BepInEx') | Out-Null
    Copy-Item -LiteralPath (Join-Path $PackageRoot 'BepInEx/core') -Destination (Join-Path $testRoot 'BepInEx') -Recurse
    $exe = Join-Path $testRoot $exeName
    function Invoke-Check([string] $Label, [int] $ExpectedExit, [string[]] $Required, [string[]] $Forbidden = @()) {
        $output = (& $exe --mod-diagnostics 2>&1 | Out-String)
        $code = $LASTEXITCODE
        if ($code -ne $ExpectedExit) { throw "$Label exit=$code; expected=$ExpectedExit`n$output" }
        foreach ($text in $Required) { if (-not $output.Contains($text)) { throw "$Label missing '$text':`n$output" } }
        foreach ($text in $Forbidden) { if ($output.Contains($text)) { throw "$Label unexpectedly contains '$text':`n$output" } }
        Write-Output "PASS $Label"
        Write-Output $output.Trim()
    }
    Invoke-Check 'empty preloader' 0 @('Preloader: Completed', 'BepInEx started: True', 'Preloader patched assemblies: 0')
    foreach ($fixture in @('Patcher', 'Plugin')) {
        $directory = if ($fixture -eq 'Patcher') { 'patchers' } else { 'plugins' }
        $destination = Join-Path $testRoot "BepInEx/$directory/Probe"
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $repoRoot "Examples/PreloaderProbe/$fixture/bin/$Configuration/net9.0/RTXSandbox.PreloaderProbe.$fixture.dll") -Destination $destination
    }
    $gameFile = Join-Path $testRoot 'Managed/RTXSandbox.dll'
    $originalHash = (Get-FileHash -LiteralPath $gameFile).Hash
    Invoke-Check 'real metadata patch and plugin' 0 @('PRELOADER_GAME_ENTRY_PATCHED', 'PRELOADER_PLUGIN_VERIFIED', 'Preloader patched assemblies: 1')
    Invoke-Check 'repeat launch with cache' 0 @('PRELOADER_PLUGIN_VERIFIED', 'Preloader patched assemblies: 1')
    if ((Get-FileHash -LiteralPath $gameFile).Hash -ne $originalHash) { throw 'Patcher modified the game DLL on disk.' }
    $directOutput = (& dotnet $gameFile --mod-diagnostics 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 1 -or -not $directOutput.Contains('Direct RTXSandbox.dll launch is too late')) { throw "Direct launch guard failed:`n$directOutput" }
    Write-Output 'PASS direct launch guard; on-disk game preserved'
    $env:RTX_SANDBOX_PROBE_FAIL = '1'
    Invoke-Check 'failed patch blocks partial startup' 1 @('Deliberate patcher failure', 'RTX Sandbox startup failed') @('PRELOADER_GAME_ENTRY_PATCHED', 'Chainloader startup complete')
    $env:RTX_SANDBOX_DISABLE_BEPINEX = '1'
    Invoke-Check 'disabled framework recovery' 0 @('Preloader: Disabled', 'BepInEx started: False') @('PRELOADER_GAME_ENTRY_PATCHED')
    Write-Output 'All preloader integration checks passed.'
}
finally {
    $env:RTX_SANDBOX_DISABLE_BEPINEX = $originalDisable
    $env:RTX_SANDBOX_PROBE_FAIL = $originalFail
    $env:RTX_SANDBOX_OUTPUT_ROOT = $originalOutput
    $env:RTX_SANDBOX_PLUGIN_ROOT = $originalPlugins
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if ($resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolved).StartsWith('rtxsandbox-preloader-')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
