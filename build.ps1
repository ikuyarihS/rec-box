<#
.SYNOPSIS
  Builds RecBox and packs a one-click installer (Setup.exe) plus an update feed with Velopack.

.EXAMPLE
  ./build.ps1 -Version 1.0.0
  ./build.ps1 -Version 1.1.0 -UpdateUrl https://github.com/you/recbox

  Output: Releases/RecBox-win-Setup.exe (give this to people), RecBox-win-Portable.zip,
  and the update feed files (releases.win.json, *.nupkg). Upload the whole Releases folder
  to the update location (a GitHub release via `dotnet vpk upload github`, or any static web folder).

  Set RECBOX_ARTIFACTS to build somewhere other than ./artifacts (Releases goes next to it).
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$UpdateUrl = "",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$artifacts = if ($env:RECBOX_ARTIFACTS) { $env:RECBOX_ARTIFACTS } else { Join-Path $root 'artifacts' }
$publish = Join-Path $artifacts "publish-$Runtime"
$releases = if ($env:RECBOX_ARTIFACTS) { Join-Path $artifacts 'Releases' } else { Join-Path $root 'Releases' }

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Check-Exit($what) { if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" } }

Push-Location $root
try {
    Step "restoring tools (vpk)"
    dotnet tool restore
    Check-Exit 'dotnet tool restore'

    Step "publishing RecBox $Version ($Runtime, self-contained)"
    dotnet publish src/RecBox/RecBox.csproj -c Release -r $Runtime --self-contained true `
        -p:Version=$Version -p:UpdateUrl=$UpdateUrl -o $publish
    Check-Exit 'dotnet publish'

    Step "packing installer and update feed"
    # "--opt=value" form: Windows PowerShell splits "--opt value" pairs in a way vpk rejects
    $icon = Join-Path $root 'src/RecBox/Assets/recbox.ico'
    dotnet vpk pack "--packId=RecBox" "--packVersion=$Version" "--packDir=$publish" "--mainExe=RecBox.exe" `
        "--packTitle=RecBox" "--icon=$icon" "--outputDir=$releases"
    Check-Exit 'vpk pack'

    Step "done"
    Get-ChildItem $releases | Where-Object { $_.Name -like "*$Version*" -or $_.Name -like '*Setup*' -or $_.Name -like 'releases.*' } |
        Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
    Write-Host "Installer: $(Join-Path $releases 'RecBox-win-Setup.exe')"
}
finally {
    Pop-Location
}
