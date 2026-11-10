[CmdletBinding()]
param(
    [string]$ManifoldGardenRoot = 'C:\D\games\Manifold Garden',
    [string]$RtxSandboxRoot = 'C:\D\games\RTXSandbox'
)

$ErrorActionPreference = 'Stop'
$manifoldExecutable = Join-Path $ManifoldGardenRoot 'ManifoldGarden.exe'
$rtxExecutable = Join-Path $RtxSandboxRoot 'RTXSandbox.exe'

foreach ($path in @($manifoldExecutable, $rtxExecutable)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Game executable was not found: $path"
    }
}

$manifold = Get-Process -Name 'ManifoldGarden' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $manifold) {
    $manifold = Start-Process -FilePath $manifoldExecutable -WorkingDirectory $ManifoldGardenRoot -PassThru
}

$rtx = Get-Process -Name 'RTXSandbox' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $rtx) {
    $previousBridgeMode = [Environment]::GetEnvironmentVariable('RTX_SANDBOX_MANIFOLD_BRIDGE', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('RTX_SANDBOX_MANIFOLD_BRIDGE', '1', 'Process')
        $rtx = Start-Process -FilePath $rtxExecutable -WorkingDirectory $RtxSandboxRoot -PassThru
    }
    finally {
        [Environment]::SetEnvironmentVariable('RTX_SANDBOX_MANIFOLD_BRIDGE', $previousBridgeMode, 'Process')
    }
}

Write-Output "MANIFOLD_PID=$($manifold.Id)"
Write-Output "RTX_PID=$($rtx.Id)"
Write-Output 'MODE=ExternalBridge'
