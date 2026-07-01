#!/usr/bin/env pwsh
# Build the Argus HA add-on image locally (multi-arch) and push to GHCR.
# Mirrors .github/workflows/build.yml but runs on the workstation instead of CI.
#
# Usage:
#   ./deploy/build-push.ps1 -Version 2.0.6
#   ./deploy/build-push.ps1 -Version 2.0.6 -SkipPublish   # reuse existing publish output
#
# Prereqs (one-time):
#   docker login ghcr.io -u krzyl2                          # PAT with write:packages
#   docker run --privileged --rm tonistiigi/binfmt --install all
#   docker buildx create --use
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$Image = 'ghcr.io/krzyl2/argus',
    [string]$Platforms = 'linux/amd64,linux/arm64',
    [switch]$SkipConfigSync,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$configPath = Join-Path $repoRoot 'argus/config.yaml'
$csproj     = 'orchestrator/Argus.Orchestrator/Argus.Orchestrator.csproj'
$publishDir = 'orchestrator/publish'

# 1. Keep config.yaml version == image tag (HA reads this from the default branch).
if (-not $SkipConfigSync) {
    $config = Get-Content $configPath -Raw
    $config = [regex]::Replace($config, '(?m)^version:\s*".*"\s*$', "version: `"$Version`"")
    Set-Content -Path $configPath -Value $config -NoNewline
    Write-Host "config.yaml -> version `"$Version`""
}

# 2. Publish orchestrator BEFORE docker build (Dockerfile COPYs orchestrator/publish/).
#    Web SDK bundles wwwroot/ automatically; assert it landed.
if (-not $SkipPublish) {
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    dotnet publish $csproj -c Release --self-contained false -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
    foreach ($f in @("$publishDir/wwwroot/js/htmx.min.js", "$publishDir/wwwroot/css/argus.css")) {
        if (-not (Test-Path $f)) { throw "missing publish asset: $f (wwwroot not in publish output)" }
    }
    Write-Host "publish OK (wwwroot assets present)"
}

# 3. Multi-arch buildx build + push to GHCR.
$buildDate = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$buildRef  = (git rev-parse HEAD).Trim()

docker buildx build `
    --platform $Platforms `
    -f argus/Dockerfile `
    -t "${Image}:$Version" `
    -t "${Image}:latest" `
    --build-arg BUILD_VERSION=$Version `
    --build-arg BUILD_DATE=$buildDate `
    --build-arg BUILD_REF=$buildRef `
    --push .
if ($LASTEXITCODE -ne 0) { throw "docker buildx build failed" }

Write-Host ""
Write-Host "Pushed ${Image}:$Version (+ :latest)"
Write-Host "Next: commit argus/config.yaml to master, then Update the add-on in HA."
