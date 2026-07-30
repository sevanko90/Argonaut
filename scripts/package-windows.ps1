# Publishes Argonaut and packs it into a Velopack release for Windows: a Setup.exe installer
# and a .nupkg update payload (see docs/velopack-auto-update-plan.md). `vpk pack` handles the
# Squirrel-style install layout itself; there is no hand-assembly step like macOS's .app bundle.
#
# Usage: scripts/package-windows.ps1 [-Rid win-x64] [-Version 1.4.0]
#   Version must be a clean SemVer (e.g. 1.4.0, no leading 'v') when packing for release;
#   omitted (or during local/dev runs) it defaults to 0.0.0.

param(
    [string]$Rid = "win-x64",
    [string]$RawVersion = ""
)

$ErrorActionPreference = "Stop"

$AppName = "Argonaut"
$Configuration = "Release"
$RootDir = (Resolve-Path "$PSScriptRoot/..").Path
$Project = Join-Path $RootDir "Argonaut/Argonaut.csproj"
$PublishDir = Join-Path $RootDir "Argonaut/bin/$Configuration/net10.0/$Rid/publish"
$DistDir = Join-Path $RootDir "dist"
$VelopackOutDir = Join-Path $DistDir "velopack"

if ([string]::IsNullOrEmpty($RawVersion)) {
    $PackVersion = "0.0.0"
} else {
    $PackVersion = $RawVersion -replace '^v', ''
    if ($PackVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
        Write-Error "version '$RawVersion' is not a clean SemVer tag (expected vX.Y.Z). Velopack needs a parseable version per package - retag the release as vX.Y.Z."
        exit 1
    }
}

$env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Host "Installing vpk CLI..."
    dotnet tool install --global vpk --version 1.2.0
}

Write-Host "Publishing $AppName for $Rid ($Configuration)..."
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
dotnet publish $Project `
    -c $Configuration `
    -r $Rid `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:InformationalVersion=$PackVersion

Write-Host "Packing Velopack release $PackVersion for $Rid..."
if (Test-Path $VelopackOutDir) { Remove-Item $VelopackOutDir -Recurse -Force }
vpk pack `
    --packId $AppName `
    --packVersion $PackVersion `
    --packDir $PublishDir `
    --mainExe "$AppName.exe" `
    --icon (Join-Path $RootDir "Argonaut/Assets/Icon/argonaut.ico") `
    --delta None `
    --outputDir $VelopackOutDir `
    -r $Rid

Write-Host "Done: $VelopackOutDir (Setup.exe, .nupkg, release feed)"
