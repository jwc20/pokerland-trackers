# Builds the Windows installer with Velopack.
#
#   .\build.ps1 -Version 0.1.0                 # -> releases\PokerlandTracker-win-Setup.exe
#   .\build.ps1 -Version 0.1.0 -Upload         # also publishes to the GitHub release v0.1.0 (needs $env:GITHUB_TOKEN)
#
# Prerequisites: PowerShell 7.3+, .NET 10 SDK, and `dotnet tool install -g vpk`.
# Code signing: set $env:VPK_SIGN_PARAMS to the signtool arguments, e.g.
#   /tr http://timestamp.digicert.com /td sha256 /fd sha256 /a
# and vpk signs every binary and the installer while packing. Without it,
# SmartScreen warns users on first run.
param(
    [Parameter(Mandatory = $true)] [string] $Version,
    [switch] $Upload
)

$ErrorActionPreference = "Stop"
# Make a failing dotnet/vpk stop the script too (PowerShell 7.3+).
$PSNativeCommandUseErrorActionPreference = $true
Set-Location $PSScriptRoot

$repo = "https://github.com/jwc20/pokerland-trackers"
$publish = Join-Path $PSScriptRoot "publish"
$releases = Join-Path $PSScriptRoot "releases"

dotnet test Pokerland.Tracker.Tests -c Release
dotnet publish Pokerland.Tracker.App -c Release -o $publish /p:Version=$Version

$packArgs = @(
    "pack",
    "--packId", "PokerlandTracker",
    "--packVersion", $Version,
    "--packDir", $publish,
    "--mainExe", "PokerlandTracker.exe",
    "--packTitle", "Pokerland Tracker",
    "--packAuthors", "Pokerland",
    "--outputDir", $releases
)
if ($env:VPK_SIGN_PARAMS) { $packArgs += @("--signParams", $env:VPK_SIGN_PARAMS) }
if ($Upload) {
    # Delta packages are built against the previous release. The first release has none,
    # so a failure here is not fatal.
    try {
        vpk download github --repoUrl $repo --token $env:GITHUB_TOKEN --outputDir $releases
    } catch {
        Write-Warning "no previous release downloaded; building full packages only"
    }
}
vpk @packArgs

if ($Upload) {
    vpk upload github --repoUrl $repo --token $env:GITHUB_TOKEN --outputDir $releases `
        --publish --merge --tag "v$Version" --releaseName "v$Version"
}
