#!/usr/bin/env pwsh
# Build the Argus HA add-on image locally (multi-arch) and push to GHCR.
# Mirrors .github/workflows/build.yml but runs on the workstation instead of CI.
#
# Usage:
#   ./deploy/build-push.ps1 -Version 2.0.6
#   ./deploy/build-push.ps1 -Version 2.0.9 -Dev           # amd64-only, faster (skips arm64/QEMU)
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
    # -SkipPublish is a no-op kept for CLI back-compat. The orchestrator is published
    # inside argus/Dockerfile's dotnet-build stage now (Phase 7 SPA migration) — there
    # is no host-side publish step left to skip.
    [switch]$SkipPublish,
    # -Dev: amd64-only build. Skips the slow arm64/QEMU leg for fast HA iteration on
    # an amd64 host. Do NOT use for real releases (arm64 users would get a stale :latest).
    [switch]$Dev
)

# -Dev forces single-arch amd64 unless the caller passed an explicit -Platforms override.
if ($Dev -and -not $PSBoundParameters.ContainsKey('Platforms')) {
    $Platforms = 'linux/amd64'
    Write-Host "-Dev: building amd64-only ($Platforms)"
}

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$configPath = Join-Path $repoRoot 'argus/config.yaml'

# 1. Keep config.yaml version == image tag (HA reads this from the default branch).
if (-not $SkipConfigSync) {
    $config = Get-Content $configPath -Raw
    $config = [regex]::Replace($config, '(?m)^version:\s*".*"\s*$', "version: `"$Version`"")
    Set-Content -Path $configPath -Value $config -NoNewline
    Write-Host "config.yaml -> version `"$Version`""
}

# 2. Multi-arch buildx build + push to GHCR.
#    argus/Dockerfile publishes the orchestrator itself (dotnet-build stage), after
#    building the SPA (ui-build stage) and copying it into wwwroot first — no host-side
#    dotnet publish or npm build needed here.
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
