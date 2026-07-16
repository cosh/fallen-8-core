# Regenerates the pinned OpenAPI snapshot (features/done/web-ui/openapi-v0.1.json) - the
# REST-contract source of truth the web-ui contract test and the mcp-server spec read.
# Replaces the by-hand "run the app, curl the doc" procedure every feature plan repeated.
# Usage: pwsh scripts/update-openapi-snapshot.ps1   (from the repo root)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$snapshot = Join-Path $root "features/done/web-ui/openapi-v0.1.json"
$url = "http://127.0.0.1:5078/openapi/v0.1.json"

dotnet build (Join-Path $root "fallen-8-core.sln") -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:Fallen8__Durability__Volatile = "true"
$app = Start-Process dotnet -ArgumentList "run --project `"$(Join-Path $root 'fallen-8-core-apiApp')`" --no-build --urls http://127.0.0.1:5078" -PassThru -NoNewWindow

try {
    $doc = $null
    foreach ($attempt in 1..30) {
        Start-Sleep -Seconds 1
        try { $doc = (Invoke-WebRequest $url -UseBasicParsing).Content; break } catch { }
    }
    if (-not $doc) { throw "the app did not serve $url within 30s" }

    [System.IO.File]::WriteAllText($snapshot, $doc, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "snapshot written: $snapshot"
}
finally {
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
}

# The additive-only check at a glance: deletions other than known doc-text catch-ups need a look.
git -C $root diff --stat -- $snapshot
