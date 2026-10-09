# Builds the release archive: dist/NivalisPerformanceFix-<version>.zip, laid out to extract into the game folder.
# usage: pwsh tools/package.ps1 [-GameDir "C:\...\Nivalis Nights"]
param([string]$GameDir = "")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root "src/NivalisPerformanceFix/NivalisPerformanceFix.csproj"

$buildArgs = @("build", $proj, "-c", "Release", "-p:InstallToGame=false")
if ($GameDir) { $buildArgs += "-p:GameDir=$GameDir" }
dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$dll = Join-Path $root "src/NivalisPerformanceFix/bin/Release/net6.0/NivalisPerformanceFix.dll"

$stage = Join-Path $root "dist/stage"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
$pluginDir = New-Item -ItemType Directory -Force (Join-Path $stage "BepInEx/plugins/NivalisPerformanceFix")
Copy-Item $dll $pluginDir
foreach ($f in "README.md", "CHANGELOG.md", "LICENSE") { Copy-Item (Join-Path $root $f) $pluginDir }

$zip = Join-Path $root "dist/NivalisPerformanceFix-$version.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
Remove-Item $stage -Recurse -Force
Write-Host "Created $zip"
