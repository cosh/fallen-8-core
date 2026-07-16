# Regenerates the pinned OpenAPI snapshot (features/done/web-ui/openapi-v0.1.json) - the
# REST-contract source of truth the web-ui contract test and the mcp-server spec read.
# Replaces the by-hand "run the app, curl the doc" procedure every feature plan repeated.
# Usage: powershell -File scripts/update-openapi-snapshot.ps1   (from anywhere)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$snapshot = Join-Path $root "features/done/web-ui/openapi-v0.1.json"
$url = "http://127.0.0.1:5078/openapi/v0.1.json"

# Fail fast when something already listens on 5078 - polling would otherwise silently
# snapshot a STALE pre-existing instance instead of the freshly built app.
if (Get-NetTCPConnection -LocalPort 5078 -State Listen -ErrorAction SilentlyContinue) {
    throw "port 5078 is already in use - stop the running instance first (a stale listener would be snapshotted)"
}

dotnet build (Join-Path $root "fallen-8-core.sln") -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$oldEnvironment = $env:ASPNETCORE_ENVIRONMENT
$oldVolatile = $env:Fallen8__Durability__Volatile
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
    # /T kills the whole tree: `dotnet run` is only a launcher whose apphost CHILD is the
    # server - stopping the launcher alone orphans the app on the port.
    taskkill /PID $app.Id /T /F 2>$null | Out-Null

    $env:ASPNETCORE_ENVIRONMENT = $oldEnvironment
    $env:Fallen8__Durability__Volatile = $oldVolatile
}

# Review the printed diff: additions are expected; a removal is fine ONLY when it is the
# doc-text of a remark this change deliberately edited - anything else needs a look.
git -C $root diff --stat -- $snapshot
