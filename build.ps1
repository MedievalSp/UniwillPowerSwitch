# Builds, tests and publishes uwpower.exe + UwPowerTray.exe into .\dist
# Requires the .NET 8 SDK. The output needs the .NET 8 Desktop Runtime on the target PC.
#
#   ./build.ps1             test + publish to .\dist
#   ./build.ps1 -Package    also create the release zips in .\artifacts:
#                             UniwillPowerSwitch-win-x64.zip                 (needs .NET 8 Desktop Runtime)
#                             UniwillPowerSwitch-win-x64-self-contained.zip  (runs without installing .NET)
#   ./build.ps1 -Package -SkipDist   only the zips (e.g. while the tray app is running from .\dist)
param([switch]$SkipTests, [switch]$Package, [switch]$SkipDist)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $SkipTests) {
    dotnet test UniwillPowerSwitch.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

function Publish([string]$output, [bool]$selfContained) {
    if (Test-Path $output) { Remove-Item $output -Recurse -Force }
    foreach ($project in 'src/UwPower.Cli', 'src/UwPower.Tray') {
        dotnet publish $project -c Release -r win-x64 --self-contained $selfContained -p:DebugType=none -o $output
        if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
    }
}

if (-not $SkipDist) {
    $dist = Join-Path $PSScriptRoot 'dist'
    Publish $dist $false
    Write-Host "Published to $dist"
}

if ($Package) {
    $artifacts = Join-Path $PSScriptRoot 'artifacts'
    New-Item -ItemType Directory -Force $artifacts | Out-Null
    $staging = Join-Path $artifacts 'staging'

    foreach ($variant in @(@{ Name = 'UniwillPowerSwitch-win-x64'; SelfContained = $false },
                           @{ Name = 'UniwillPowerSwitch-win-x64-self-contained'; SelfContained = $true })) {
        $folder = Join-Path $staging $variant.Name
        Publish $folder $variant.SelfContained
        Copy-Item README.md, LICENSE $folder
        $zip = Join-Path $artifacts "$($variant.Name).zip"
        if (Test-Path $zip) { Remove-Item $zip }
        Compress-Archive -Path (Join-Path $folder '*') -DestinationPath $zip
        Write-Host "Packaged $zip"
    }
    Remove-Item $staging -Recurse -Force
}
