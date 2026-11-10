$ErrorActionPreference = 'Stop'

$sourceRoot = 'C:\Users\USER\source\repos\GameDev\RTXsandbox\bin\Release\net9.0'
$targetRoot = 'C:\D\games\RTXSandbox'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupRoot = "C:\Users\USER\source\repos\GameDev\RTXsandbox\artifacts\games-before-soft-visible-$stamp"

if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
    throw "Release source is missing: $sourceRoot"
}
if (-not (Test-Path -LiteralPath $targetRoot -PathType Container)) {
    throw "Installed game is missing: $targetRoot"
}
if (Get-Process -Name RTXSandbox -ErrorAction SilentlyContinue) {
    throw 'RTXSandbox is running; deployment refuses to overwrite loaded assemblies.'
}

$relativeFiles = [System.Collections.Generic.List[string]]::new()
foreach ($relative in @(
    'RTXSandbox.exe',
    'Managed\RTXSandbox.dll',
    'Managed\RTXSandbox.pdb',
    'Managed\RTXSandbox.deps.json',
    'Managed\RTXSandbox.runtimeconfig.json'
)) {
    $relativeFiles.Add($relative)
}
foreach ($folder in @('Content', 'Shaders')) {
    $folderRoot = Join-Path $sourceRoot $folder
    foreach ($file in Get-ChildItem -LiteralPath $folderRoot -File -Recurse) {
        $relativeFiles.Add([System.IO.Path]::GetRelativePath($sourceRoot, $file.FullName))
    }
}

$relativeFiles = $relativeFiles | Sort-Object -Unique
New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
$beforeSave = Get-ChildItem -LiteralPath (Join-Path $targetRoot 'Saves') -File -Recurse |
    Sort-Object FullName |
    ForEach-Object { "{0}|{1}" -f [System.IO.Path]::GetRelativePath($targetRoot, $_.FullName), (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
$beforeSettings = foreach ($name in @('settings.json', 'settings.json.bak', 'imgui.ini')) {
    $path = Join-Path $targetRoot $name
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        "{0}|{1}" -f $name, (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
}

$manifest = [System.Collections.Generic.List[string]]::new()
foreach ($relative in $relativeFiles) {
    $source = Join-Path $sourceRoot $relative
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Allowlisted release file is missing: $source"
    }
    $destination = Join-Path $targetRoot $relative
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        $backup = Join-Path $backupRoot $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backup) | Out-Null
        Copy-Item -LiteralPath $destination -Destination $backup -Force
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $targetHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    if ($sourceHash -ne $targetHash) {
        throw "Deployment hash mismatch: $relative"
    }
    $manifest.Add("$relative|$sourceHash")
}

$afterSave = Get-ChildItem -LiteralPath (Join-Path $targetRoot 'Saves') -File -Recurse |
    Sort-Object FullName |
    ForEach-Object { "{0}|{1}" -f [System.IO.Path]::GetRelativePath($targetRoot, $_.FullName), (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
$afterSettings = foreach ($name in @('settings.json', 'settings.json.bak', 'imgui.ini')) {
    $path = Join-Path $targetRoot $name
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        "{0}|{1}" -f $name, (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
}
if (Compare-Object $beforeSave $afterSave) {
    throw 'Save data changed during deployment.'
}
if (Compare-Object $beforeSettings $afterSettings) {
    throw 'User settings changed during deployment.'
}

$manifest | Set-Content -LiteralPath (Join-Path $backupRoot 'deployed-sha256.txt') -Encoding utf8
$beforeSave | Set-Content -LiteralPath (Join-Path $backupRoot 'preserved-saves-sha256.txt') -Encoding utf8
$beforeSettings | Set-Content -LiteralPath (Join-Path $backupRoot 'preserved-settings-sha256.txt') -Encoding utf8
Write-Output "DEPLOYED=$($relativeFiles.Count)"
Write-Output "BACKUP=$backupRoot"
Write-Output "SAVES=$($beforeSave.Count)"
Write-Output "SETTINGS=$($beforeSettings.Count)"
