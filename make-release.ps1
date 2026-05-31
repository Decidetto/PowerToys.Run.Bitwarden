# Builds x64 and ARM64 Release zips for the Bitwarden PT Run plugin.
# Output zips land in .\release-output\ at the repo root.
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$project = Join-Path $root 'PowerToys.Run.Bitwarden'
$projectFile = Join-Path $project 'PowerToys.Run.Bitwarden.csproj'
$pluginJson = Join-Path $project 'plugin.json'
$tfm = 'net9.0-windows10.0.22621.0'

# Version is sourced from plugin.json (the manifest PT reads); csproj <Version> should match.
$version = (Get-Content $pluginJson -Raw | ConvertFrom-Json).Version
if (-not $version) { throw "Couldn't read Version from $pluginJson" }
Write-Host "Bitwarden v$version" -ForegroundColor Cyan

# Files included in the release zip, paths relative to the build output dir. PDB is
# opt-in for now -- helpful for line numbers in stack traces during the v0.1.x stretch;
# drop from this list once bug reports settle down.
$shipFiles = @(
    'plugin.json',
    'PowerToys.Run.Bitwarden.dll',
    'PowerToys.Run.Bitwarden.deps.json',
    'PowerToys.Run.Bitwarden.pdb',
    'Images\Bitwarden.dark.png',
    'Images\Bitwarden.light.png'
)

$stagingRoot = Join-Path $root 'release-staging'
$outputRoot = Join-Path $root 'release-output'
if (Test-Path $stagingRoot) { Remove-Item $stagingRoot -Recurse -Force }
if (Test-Path $outputRoot) { Remove-Item $outputRoot -Recurse -Force }
New-Item -ItemType Directory -Path $stagingRoot, $outputRoot | Out-Null

foreach ($platform in 'x64', 'ARM64') {
    Write-Host "`n=== Building $platform ===" -ForegroundColor Cyan
    & dotnet build $projectFile -c Release -p:Platform=$platform --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed for $platform" }

    $binDir = Join-Path $project "bin\$platform\Release\$tfm"
    # Stage under <Platform>\Bitwarden\ so the zip's top-level dir is "Bitwarden",
    # which matches the folder name PT expects under Plugins\.
    $stageDir = Join-Path $stagingRoot "$platform\Bitwarden"
    New-Item -ItemType Directory -Path $stageDir | Out-Null

    foreach ($f in $shipFiles) {
        $src = Join-Path $binDir $f
        if (-not (Test-Path $src)) { throw "Missing expected build artifact: $src" }
        $dst = Join-Path $stageDir $f
        $dstDir = Split-Path $dst -Parent
        if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Path $dstDir | Out-Null }
        Copy-Item $src $dst
    }

    $archLower = $platform.ToLowerInvariant()
    $zipPath = Join-Path $outputRoot "Bitwarden-v$version-$archLower.zip"
    Compress-Archive -Path $stageDir -DestinationPath $zipPath -Force
    Write-Host "  -> $zipPath" -ForegroundColor Green
}

Write-Host "`nRelease zips:" -ForegroundColor Cyan
Get-ChildItem $outputRoot | ForEach-Object {
    $kb = [math]::Round($_.Length / 1KB, 1)
    Write-Host "  $($_.Name) ($kb KB)"
}

Remove-Item $stagingRoot -Recurse -Force
