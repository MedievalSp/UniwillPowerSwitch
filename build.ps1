# Builds, tests and publishes uwpower.exe + UwPowerTray.exe into .\dist
# Requires the .NET 8 SDK. The output needs the .NET 8 Desktop Runtime on the target PC.
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $SkipTests) {
    dotnet test UniwillPowerSwitch.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

$dist = Join-Path $PSScriptRoot 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
foreach ($project in 'src/UwPower.Cli', 'src/UwPower.Tray') {
    dotnet publish $project -c Release -r win-x64 --self-contained false -p:DebugType=none -o $dist
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}
Write-Host "Published to $dist"
