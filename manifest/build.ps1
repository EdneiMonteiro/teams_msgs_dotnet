# Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
# See LICENSE and DISCLAIMER.md in the project root for details.
#
# Gera manifest/build/teams-msgs-dotnet-app.zip a partir do template.
#
# Uso:
#   pwsh ./manifest/build.ps1 -AppId <APP_ID> -Fqdn <FQDN>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $AppId,
    [Parameter(Mandatory = $true)] [string] $Fqdn,
    [string] $OutputDir = (Join-Path $PSScriptRoot 'build')
)

$ErrorActionPreference = 'Stop'

$manifestSrc = Join-Path $PSScriptRoot 'manifest.json'
$colorPng    = Join-Path $PSScriptRoot 'color.png'
$outlinePng  = Join-Path $PSScriptRoot 'outline.png'

if (-not (Test-Path $manifestSrc)) {
    throw "Arquivo $manifestSrc não encontrado."
}

New-Item -ItemType Directory -Force $OutputDir | Out-Null

$content = (Get-Content $manifestSrc -Raw) `
    -replace '<MICROSOFT_APP_ID>', $AppId `
    -replace '<your-api-fqdn>', $Fqdn

$manifestOut = Join-Path $OutputDir 'manifest.json'
$content | Set-Content -Encoding UTF8 $manifestOut

Copy-Item $colorPng (Join-Path $OutputDir 'color.png') -Force
Copy-Item $outlinePng (Join-Path $OutputDir 'outline.png') -Force

$zip = Join-Path $OutputDir 'teams-msgs-dotnet-app.zip'
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive `
    -Path (Join-Path $OutputDir 'manifest.json'),
          (Join-Path $OutputDir 'color.png'),
          (Join-Path $OutputDir 'outline.png') `
    -DestinationPath $zip

Write-Host "Gerado: $zip"
