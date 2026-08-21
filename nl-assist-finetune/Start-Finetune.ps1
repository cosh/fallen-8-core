# MIT License
#
# Start-Finetune.ps1
#
# Copyright (c) 2011-2026 Henning Rauch
#
# Permission is hereby granted, free of charge, to any person obtaining a copy
# of this software and associated documentation files (the "Software"), to deal
# in the Software without restriction, including without limitation the rights
# to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
# copies of the Software, and to permit persons to whom the Software is
# furnished to do so, subject to the following conditions:
#
# The above copyright notice and this permission notice shall be included in all
# copies or substantial portions of the Software.
#
# THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
# IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
#
# FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
# AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
# LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
# OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
# SOFTWARE.

<#
.SYNOPSIS
  Windows launch box for a fine-tune round: preflight, consolidate captured feedback, then hand
  the whole session to the Azure A10 runner (infra/deploy.sh).

.DESCRIPTION
  The ordered procedure and the cross-machine carry live in RUNBOOK.md; the pipeline itself is
  explained in README.md and infra/README.md. This script automates only the Windows side, and
  refuses to launch when a precondition the runner cannot recover from is missing.

  Why each blocking check exists (every one is a failure mode that would cost a run):
   - deploy.sh clones REPO_REF from the REMOTE, so an unpushed commit is not trained.
   - deploy.sh derives REPO_URL/REPO_REF with "git -C ... || echo main"; under WSL on /mnt/c a
     dubious-ownership error makes that fall back to main silently, training the wrong branch.
     Both are computed here (where git works) and passed explicitly.
   - $HOME differs between WSL, Git Bash and Windows, so the SSH and Ollama key paths are
     resolved here and passed explicitly instead of left to the shell's ~.
   - az must be callable INSIDE the launcher. A Windows-only Azure CLI is not usable from WSL
     (the extensionless shim is an MSYS script), which the probe below catches.

  What is NOT a risk, and why no check guards it: dataset/ is gitignored, so the VM's clone has
  no train.jsonl and regenerates it from the current contract sources against the apiApp it
  starts itself. A stale corpus cannot travel. dataset/captured.jsonl is the exception - it is
  carried from this box by deploy.sh, which is what the Consolidate stage produces.

.PARAMETER Stage
  Preflight (default, checks only) | Consolidate | Azure | Run (consolidate then launch) |
  Local (prints the WSL command sequence for a local-GPU run; nothing is executed).

.EXAMPLE
  powershell -File nl-assist-finetune\Start-Finetune.ps1

.EXAMPLE
  powershell -File nl-assist-finetune\Start-Finetune.ps1 -Stage Run -PublishPrefix myns -CapturesFrom D:\carry
#>
[CmdletBinding()]
param(
    [ValidateSet('Preflight', 'Consolidate', 'Azure', 'Run', 'Local', 'Eval')]
    [string]$Stage = 'Preflight',
    [string]$PublishPrefix,
    [string]$Variants = 'phi4-f8-mini phi4-f8',
    [string]$Location = 'westeurope',
    [switch]$Spot,
    [string]$CapturesFrom,
    [string]$EvalPrefix,
    [string]$EvalBaselines = 'phi4-mini',
    [string]$AttachRg,
    [int]$EvalWaitMin = 180,
    [string]$SshPubKeyFile,
    [string]$SshKeyFile,
    [string]$OllamaKeyFile,
    [ValidateSet('Auto', 'Wsl', 'GitBash')]
    [string]$Launcher = 'Auto',
    [int]$ApiPort = 5000,
    [int]$ApiTimeoutSeconds = 420,
    [switch]$UseExistingApi,
    [switch]$NoPublish,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Ft = $PSScriptRoot
$script:Checks = @()
$script:FailsByScope = @{ Tools = 0; Repo = 0; Azure = 0 }

function Write-Head([string]$Text) { Write-Host "`n== $Text ==" -ForegroundColor Cyan }
function Write-Note([string]$Text) { Write-Host "   $Text" -ForegroundColor DarkGray }

# Scope decides WHICH stages a failure blocks: Repo (what origin has) is irrelevant to
# consolidating captures on this box, and Azure is irrelevant unless a VM is being created.
function Add-Check([string]$Name, [bool]$Ok, [string]$Detail, [string]$Scope = 'Tools', [switch]$Soft) {
    if ($Ok) { $state = 'OK' }
    elseif ($Soft) { $state = 'WARN' }
    else { $state = 'FAIL'; $script:FailsByScope[$Scope]++ }
    $script:Checks += [pscustomobject]@{ Check = $Name; State = $state; Scope = $Scope; Detail = $Detail }
}

# C:\x\y -> /mnt/c/x/y (WSL) or /c/x/y (Git Bash / MSYS).
function ConvertTo-LauncherPath([string]$WindowsPath, [string]$Kind) {
    if ([string]::IsNullOrWhiteSpace($WindowsPath)) { return '' }
    $full = [IO.Path]::GetFullPath($WindowsPath)
    if ($full.StartsWith('\\')) { throw "UNC path not usable from ${Kind}: $full. Use a local checkout." }
    $drive = $full.Substring(0, 1).ToLower()
    $rest = $full.Substring(2).Replace('\', '/')
    if ($Kind -eq 'Wsl') { return "/mnt/$drive$rest" }
    return "/$drive$rest"
}

function Resolve-Launcher([string]$Preference) {
    if ($Preference -ne 'GitBash') {
        $wsl = Get-Command wsl.exe -ErrorAction SilentlyContinue
        if ($wsl) {
            # "wsl -l -q" emits UTF-16LE; PowerShell 5.1 keeps the NULs, so strip them first.
            $raw = (& wsl.exe -l -q) -join ''
            $distros = ($raw -replace "`0", '').Trim()
            if ($LASTEXITCODE -eq 0 -and $distros.Length -gt 0) {
                return [pscustomobject]@{ Kind = 'Wsl'; Exe = $wsl.Source; Distro = $distros }
            }
        }
        if ($Preference -eq 'Wsl') { throw 'No WSL distro found ("wsl -l -q" is empty). Install one, or use -Launcher GitBash.' }
    }
    $candidates = @()
    $gb = Get-Command bash.exe -ErrorAction SilentlyContinue
    if ($gb) { $candidates += $gb.Source }
    $candidates += (Join-Path $env:ProgramFiles 'Git\bin\bash.exe')
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return [pscustomobject]@{ Kind = 'GitBash'; Exe = $c; Distro = '' } }
    }
    throw 'No bash found: neither a WSL distro nor Git Bash. deploy.sh needs one of them.'
}

# Runs a bash body through the launcher. The body goes into a temp .sh (LF, UTF-8 without BOM)
# rather than -c, so nothing has to survive two layers of quoting.
function Invoke-Bash([pscustomobject]$L, [string]$Body, [switch]$Capture) {
    $tmp = Join-Path $env:TEMP ('f8-ft-' + [guid]::NewGuid().ToString('N') + '.sh')
    $enc = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($tmp, ($Body -replace "`r`n", "`n"), $enc)
    $inner = ConvertTo-LauncherPath $tmp $L.Kind
    try {
        if ($L.Kind -eq 'Wsl') { $argv = @('-e', 'bash', $inner) } else { $argv = @($inner) }
        if ($Capture) { return (& $L.Exe $argv) }
        & $L.Exe $argv
        return $null
    }
    finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
}

function Test-ApiAnswering([int]$Port) {
    try {
        $r = Invoke-WebRequest -Uri "http://localhost:$Port/status" -UseBasicParsing -TimeoutSec 5
        return ($r.StatusCode -eq 200)
    }
    catch { return $false }
}

function Get-LineCount([string]$Path) {
    if (-not (Test-Path $Path)) { return 0 }
    return (Get-Content -LiteralPath $Path | Measure-Object -Line).Lines
}

function Resolve-KeyPath([string]$Explicit, [string[]]$Fallbacks) {
    if ($Explicit) { return $Explicit }
    foreach ($f in $Fallbacks) { if (Test-Path $f) { return $f } }
    return ''
}

# --- preflight -----------------------------------------------------------------------------------

function Invoke-Preflight([pscustomobject]$L, [string]$Job) {
    Write-Head "preflight (launcher: $($L.Kind) $($L.Distro))"

    foreach ($t in @('git', 'dotnet', 'node', 'npm')) {
        $c = Get-Command $t -ErrorAction SilentlyContinue
        if ($c) { Add-Check "windows: $t" $true $c.Source }
        else { Add-Check "windows: $t" $false 'not on PATH' }
    }

    Push-Location $RepoRoot
    try {
        $branch = (& git rev-parse --abbrev-ref HEAD).Trim()
        $head = (& git rev-parse HEAD).Trim()
        $script:Branch = $branch
        $script:RepoUrl = ((& git config --get remote.origin.url) -replace '^git@github\.com:', 'https://github.com/') -replace '^ssh://git@github\.com/', 'https://github.com/'

        # The VM trains what origin has, so the direction of any divergence decides the verdict:
        # ahead (unpushed) is fatal, behind only means you are not looking at what will train.
        $lsRemote = (& git ls-remote origin ('refs/heads/' + $branch)) -join "`n"
        if ([string]::IsNullOrWhiteSpace($lsRemote)) {
            Add-Check 'git: branch on origin' $false "'$branch' is not on origin - the VM clones REPO_REF, so push it first" 'Repo'
        }
        else {
            $remoteSha = ($lsRemote -split '\s+')[0]
            $shortLocal = $head.Substring(0, 8)
            $shortRemote = $remoteSha.Substring(0, 8)
            if ($remoteSha -eq $head) {
                Add-Check 'git: branch on origin' $true "$branch @ $shortLocal" 'Repo'
            }
            else {
                # --verify --quiet stays silent when the object is absent (no stderr to swallow).
                $prev = $ErrorActionPreference
                $ErrorActionPreference = 'Continue'
                & git rev-parse --verify --quiet ($remoteSha + '^{commit}') | Out-Null
                $haveRemote = ($LASTEXITCODE -eq 0)
                $behind = $false
                $ahead = $false
                if ($haveRemote) {
                    & git merge-base --is-ancestor $head $remoteSha
                    $behind = ($LASTEXITCODE -eq 0)
                    & git merge-base --is-ancestor $remoteSha $head
                    $ahead = ($LASTEXITCODE -eq 0)
                }
                $ErrorActionPreference = $prev

                if (-not $haveRemote) {
                    Add-Check 'git: branch on origin' $false "origin is at $shortRemote, not in this checkout - run 'git fetch' and re-run; until then it is unknowable whether you have unpushed commits" 'Repo'
                }
                elseif ($ahead) {
                    Add-Check 'git: branch on origin' $false "local $shortLocal is AHEAD of origin $shortRemote - unpushed commits are NOT trained; push first" 'Repo'
                }
                elseif ($behind) {
                    Add-Check 'git: branch on origin' $false "local $shortLocal is BEHIND origin $shortRemote - the VM would train origin's newer commit, not what you see here; pull to match" 'Repo' -Soft
                }
                else {
                    Add-Check 'git: branch on origin' $false "local $shortLocal and origin $shortRemote have DIVERGED - reconcile before training" 'Repo'
                }
            }
        }

        $dirty = (& git status --porcelain) -join "`n"
        if ([string]::IsNullOrWhiteSpace($dirty)) { Add-Check 'git: working tree' $true 'clean' 'Repo' }
        else { Add-Check 'git: working tree' $false 'uncommitted changes never reach the VM (it clones)' 'Repo' -Soft }
    }
    finally { Pop-Location }

    $inboxDir = Join-Path $Ft 'feedback\inbox'
    $inboxCount = 0
    if (Test-Path $inboxDir) { $inboxCount = @(Get-ChildItem $inboxDir -Filter '*.jsonl' -ErrorAction SilentlyContinue).Count }
    $capturedRows = Get-LineCount (Join-Path $Ft 'dataset\captured.jsonl')
    if ($inboxCount -gt 0 -or $capturedRows -gt 0) {
        Add-Check 'feedback: field rows' $true "$inboxCount raw capture file(s) in feedback/inbox, $capturedRows row(s) in dataset/captured.jsonl" 'Repo'
    }
    else {
        Add-Check 'feedback: field rows' $false 'none on this box - the run would train on the generated dataset only (RUNBOOK step 2)' 'Repo' -Soft
    }

    if (Test-Path (Join-Path $RepoRoot 'fallen-8-web-ui\node_modules')) {
        Add-Check 'node: web-ui deps' $true 'present'
    }
    else {
        Add-Check 'node: web-ui deps' $false 'absent - the Consolidate stage runs npm ci first' -Soft
    }

    if ($Job -eq 'None') { return }

    $sshKey = Resolve-KeyPath $SshPubKeyFile @((Join-Path $HOME '.ssh\id_ed25519.pub'), (Join-Path $HOME '.ssh\id_rsa.pub'))
    $script:SshKeyResolved = $sshKey
    if ($sshKey) { Add-Check 'azure: ssh public key' $true $sshKey 'Azure' }
    else { Add-Check 'azure: ssh public key' $false 'none found; pass -SshPubKeyFile' 'Azure' }

    if ($Job -eq 'Eval') {
        # The eval job has to ssh back in to fetch the results, so the PRIVATE half is required
        # here even though the fine-tune job never needs it.
        $priv = $SshKeyFile
        if (-not $priv -and $sshKey) { $priv = ($sshKey -replace '\.pub$', '') }
        if ($priv -and (Test-Path $priv)) { Add-Check 'eval: ssh private key' $true $priv 'Azure' }
        else { Add-Check 'eval: ssh private key' $false 'not found; the run cannot fetch its results. Pass -SshKeyFile' 'Azure' }
        $script:SshPrivResolved = $priv

        if ($EvalPrefix) {
            # Cheapest possible failure: a variant that was never published. Same auth-free
            # manifest GET the publish step uses, before any VM exists.
            foreach ($v in ($Variants -split '\s+' | Where-Object { $_ })) {
                $uri = "https://registry.ollama.ai/v2/$EvalPrefix/$v/manifests/latest"
                $ok = $false
                try { $ok = ((Invoke-WebRequest -Uri $uri -UseBasicParsing -TimeoutSec 30).StatusCode -eq 200) } catch { $ok = $false }
                if ($ok) { Add-Check "eval: $EvalPrefix/$v published" $true 'manifest 200' 'Azure' }
                else { Add-Check "eval: $EvalPrefix/$v published" $false 'no latest manifest in the registry - publish it or drop it from -Variants' 'Azure' }
            }
        }
        else {
            Add-Check 'eval: registry namespace' $false '-EvalPrefix is required: the VM has no local models to evaluate' 'Azure'
        }
    }

    # Publishing is a fine-tune concern only: the eval job pulls, it never pushes.
    if ($Job -eq 'Finetune') {
        $publishing = (-not $NoPublish)
        $ollamaKey = Resolve-KeyPath $OllamaKeyFile @((Join-Path $HOME '.ollama\id_ed25519'))
        $script:OllamaKeyResolved = $ollamaKey
        if ($ollamaKey -and (Get-Item $ollamaKey).Length -gt 0) {
            Add-Check 'azure: ollama signing key' $true $ollamaKey 'Azure'
        }
        elseif ($publishing) {
            Add-Check 'azure: ollama signing key' $false 'no non-empty key; deploy.sh hard-errors when publishing. Register one at ollama.com/settings/keys, or pass -NoPublish' 'Azure'
        }
        else {
            Add-Check 'azure: ollama signing key' $false 'absent (push skipped by -NoPublish)' 'Azure' -Soft
        }

        if ($publishing -and -not $PublishPrefix) {
            Add-Check 'azure: publish target' $false '-PublishPrefix is required: with no target, a successful run self-destructs the only copy of the models' 'Azure'
        }
        elseif ($publishing) {
            Add-Check 'azure: publish target' $true "$PublishPrefix/<variant>" 'Azure'
        }
        else {
            Add-Check 'azure: publish target' $false '-NoPublish forces DESTROY_ON_FINISH=0; you must delete the resource group by hand' 'Azure' -Soft
        }
    }


    $probe = @'
for t in az jq ssh curl git base64; do
  if command -v "$t" >/dev/null 2>&1; then echo "tool:$t=ok"; else echo "tool:$t=missing"; fi
done
if command -v az >/dev/null 2>&1; then
  if az account show --query id -o tsv >/dev/null 2>&1; then
    echo "az-login=ok:$(az account show --query name -o tsv 2>/dev/null)"
  else
    echo "az-login=missing"
  fi
fi
'@
    $lines = @(Invoke-Bash $L $probe -Capture)
    foreach ($t in @('az', 'jq', 'ssh', 'curl')) {
        $hit = $lines | Where-Object { $_ -eq "tool:$t=ok" }
        if ($hit) { Add-Check "$($L.Kind): $t" $true 'found' 'Azure' }
        elseif ($t -eq 'jq') { Add-Check "$($L.Kind): $t" $false 'absent - the vCPU quota preflight is skipped without it' 'Azure' -Soft }
        else { Add-Check "$($L.Kind): $t" $false "install it inside $($L.Kind); deploy.sh calls it there" 'Azure' }
    }
    $login = $lines | Where-Object { $_ -like 'az-login=ok*' }
    if ($login) { Add-Check "$($L.Kind): az login" $true (($login -split ':', 2)[1]) 'Azure' }
    else { Add-Check "$($L.Kind): az login" $false "run 'az login' inside $($L.Kind) (a Windows-only az is not callable from WSL)" 'Azure' }
}

# --- consolidate ---------------------------------------------------------------------------------

function Invoke-Consolidate {
    Write-Head 'consolidate captured feedback (FL-3)'

    if ($CapturesFrom) {
        if (-not (Test-Path $CapturesFrom)) { throw "-CapturesFrom '$CapturesFrom' does not exist." }
        $inbox = Join-Path $Ft 'feedback\inbox'
        if (-not (Test-Path $inbox)) { New-Item -ItemType Directory -Path $inbox | Out-Null }
        $files = @(Get-ChildItem $CapturesFrom -Filter '*.jsonl')
        if ($files.Count -eq 0) { Write-Warning "no *.jsonl under '$CapturesFrom'." }
        foreach ($f in $files) { Copy-Item $f.FullName -Destination $inbox -Force }
        Write-Note "copied $($files.Count) capture file(s) into feedback/inbox."
    }

    $inboxDir = Join-Path $Ft 'feedback\inbox'
    $inboxFiles = @()
    if (Test-Path $inboxDir) { $inboxFiles = @(Get-ChildItem $inboxDir -Filter '*.jsonl') }
    if ($inboxFiles.Count -eq 0) {
        Write-Note 'feedback/inbox is empty - nothing to consolidate, skipping.'
        return
    }

    if (-not (Test-Path (Join-Path $RepoRoot 'fallen-8-web-ui\node_modules'))) {
        Write-Note 'installing fallen-8-web-ui deps (npm ci) - consolidate imports the shipping prompt modules.'
        Push-Location (Join-Path $RepoRoot 'fallen-8-web-ui')
        try {
            & npm ci
            if ($LASTEXITCODE -ne 0) { throw "npm ci failed ($LASTEXITCODE)." }
        }
        finally { Pop-Location }
    }

    $before = Get-LineCount (Join-Path $Ft 'dataset\captured.jsonl')
    $log = Join-Path $env:TEMP 'f8-apiapp-consolidate.log'

    # A foreign instance on this port is NOT a convenience. Measured while writing this script:
    # our own Release start died with "address already in use", the health poll passed against
    # the stranger anyway, and consolidate re-validated all 37 captures against an unknown Debug
    # build while reporting success. The compile authority has to be a build we chose.
    $occupied = Test-ApiAnswering $ApiPort
    if ($occupied -and -not $UseExistingApi) {
        throw ("something already answers /status on port $ApiPort. Its build is unknown, and the " +
            "compile gate decides which rows enter the corpus. Stop it, pass -ApiPort <free port>, " +
            'or pass -UseExistingApi to validate against it deliberately.')
    }

    $oldVolatile = $env:Fallen8__Durability__Volatile
    $oldUrls = $env:ASPNETCORE_URLS
    $oldEnvName = $env:ASPNETCORE_ENVIRONMENT
    $oldF8 = $env:NL_EVAL_F8
    $proc = $null
    try {
        if ($occupied) {
            Write-Warning "-UseExistingApi: validating against the instance already on port $ApiPort. Its code is not necessarily this checkout's."
        }
        else {
            $env:Fallen8__Durability__Volatile = 'true'
            $env:ASPNETCORE_URLS = "http://localhost:$ApiPort"
            # --no-launch-profile, or launchSettings.json wins: its profile pins
            # ASPNETCORE_URLS to :5000 and launchBrowser, so -ApiPort would be ignored and the
            # app would fight whatever already holds 5000. The profile's environment is set
            # here instead, so this matches what the VM's bootstrap.sh runs.
            $env:ASPNETCORE_ENVIRONMENT = 'Development'
            Write-Note "starting the apiApp (volatile) on http://localhost:$ApiPort - log: $log"
            $proc = Start-Process -FilePath 'dotnet' `
                -ArgumentList @('run', '--project', 'fallen-8-core-apiApp', '-c', 'Release', '--no-launch-profile') `
                -WorkingDirectory $RepoRoot -PassThru -NoNewWindow `
                -RedirectStandardOutput $log -RedirectStandardError ($log + '.err')

            $healthy = $false
            $deadline = (Get-Date).AddSeconds($ApiTimeoutSeconds)
            while ((Get-Date) -lt $deadline) {
                if ($proc.HasExited) { throw "the apiApp exited early (code $($proc.ExitCode)); see $log and $log.err" }
                if (Test-ApiAnswering $ApiPort) { $healthy = $true; break }
                Start-Sleep -Seconds 3
            }
            if (-not $healthy) { throw "the apiApp did not answer /status within $ApiTimeoutSeconds s; see $log" }
            Write-Note 'apiApp healthy (compile authority up, this checkout, Release).'
        }

        $env:NL_EVAL_F8 = "http://localhost:$ApiPort"
        Push-Location $RepoRoot
        try {
            & npx tsx nl-assist-finetune/feedback/consolidate.ts
            if ($LASTEXITCODE -ne 0) { throw "consolidate.ts failed ($LASTEXITCODE)." }
        }
        finally { Pop-Location }
    }
    finally {
        # "dotnet run" spawns the app as a CHILD, so kill the tree, not just the launcher.
        if ($proc -and -not $proc.HasExited) { & taskkill /PID $proc.Id /T /F | Out-Null }
        $env:Fallen8__Durability__Volatile = $oldVolatile
        $env:ASPNETCORE_URLS = $oldUrls
        $env:ASPNETCORE_ENVIRONMENT = $oldEnvName
        $env:NL_EVAL_F8 = $oldF8
    }

    $after = Get-LineCount (Join-Path $Ft 'dataset\captured.jsonl')
    Write-Note "dataset/captured.jsonl: $before -> $after row(s). consolidate.ts rebuilds each row's system prompt from the CURRENT contract, so carried rows are not stale."
}

# --- azure ---------------------------------------------------------------------------------------

function Invoke-AzureRun([pscustomobject]$L) {
    Write-Head 'launch the Azure A10 runner (infra/deploy.sh)'

    $infra = ConvertTo-LauncherPath (Join-Path $Ft 'infra') $L.Kind
    $vars = @()
    $vars += "LOCATION='$Location'"
    $vars += "VARIANTS='$Variants'"
    if ($Spot) { $vars += 'F8_SPOT=1' }
    $vars += "REPO_URL='$($script:RepoUrl)'"
    $vars += "REPO_REF='$($script:Branch)'"
    if ($script:SshKeyResolved) { $vars += "SSH_PUBKEY_FILE='$(ConvertTo-LauncherPath $script:SshKeyResolved $L.Kind)'" }
    if ($NoPublish) {
        $vars += 'DESTROY_ON_FINISH=0'
        Write-Warning 'no publish target: DESTROY_ON_FINISH=0, so the resource group survives and YOU must delete it (az group delete -n <rg> --yes).'
    }
    else {
        $vars += "PUBLISH_PREFIX='$PublishPrefix'"
        if ($script:OllamaKeyResolved) { $vars += "OLLAMA_KEY_FILE='$(ConvertTo-LauncherPath $script:OllamaKeyResolved $L.Kind)'" }
    }

    $body = "set -e`ncd '$infra'`n" + ($vars -join ' ') + " bash ./deploy.sh`n"
    Write-Note 'command:'
    Write-Host $body
    if ($DryRun) { Write-Note '-DryRun: not launching.'; return }

    Invoke-Bash $L $body
    if ($LASTEXITCODE -ne 0) { throw "deploy.sh exited $LASTEXITCODE. On failure the VM keeps itself for 1 h so the log is readable, then self-deletes; check /var/log/f8-finetune.log over ssh." }
    Write-Note 'deploy.sh finished. Next: verify the pushed models, run the phase-4 eval (README), then close the RETRAIN-LOG entries it absorbed.'
}

function Invoke-EvalRun([pscustomobject]$L) {
    Write-Head 'launch the cloud evaluation (infra/eval-deploy.sh)'

    $infra = ConvertTo-LauncherPath (Join-Path $Ft 'infra') $L.Kind
    $vars = @()
    if ($AttachRg) {
        # Re-attach: everything else is already encoded in the running deployment.
        $vars += "EVAL_ATTACH_RG='$AttachRg'"
    }
    else {
        $vars += "EVAL_PREFIX='$EvalPrefix'"
        $vars += "VARIANTS='$Variants'"
        $vars += "EVAL_BASELINES='$EvalBaselines'"
        $vars += "LOCATION='$Location'"
        if ($Spot) { $vars += 'F8_SPOT=1' }
        $vars += "REPO_URL='$($script:RepoUrl)'"
        $vars += "REPO_REF='$($script:Branch)'"
    }
    $vars += "EVAL_WAIT_MIN='$EvalWaitMin'"
    if ($script:SshKeyResolved) { $vars += "SSH_PUBKEY_FILE='$(ConvertTo-LauncherPath $script:SshKeyResolved $L.Kind)'" }
    if ($script:SshPrivResolved) { $vars += "SSH_KEY_FILE='$(ConvertTo-LauncherPath $script:SshPrivResolved $L.Kind)'" }

    $body = "set -e`ncd '$infra'`n" + ($vars -join ' ') + " bash ./eval-deploy.sh`n"
    Write-Note 'command:'
    Write-Host $body
    if ($DryRun) { Write-Note '-DryRun: not launching.'; return }

    Write-Note "this stage WAITS (up to $EvalWaitMin min) because the results have to be copied down before the"
    Write-Note 'resource group is deleted. If this box sleeps, re-attach with -Stage Eval -AttachRg <rg>.'
    Invoke-Bash $L $body
    if ($LASTEXITCODE -ne 0) { throw "eval-deploy.sh exited $LASTEXITCODE - see the output above; the resource group was deliberately kept unless it printed otherwise." }
}

# --- local (documented, deliberately not automated) ----------------------------------------------

function Show-LocalPlan {
    Write-Head 'local-GPU run (not automated by this script)'
    Write-Host @'
The 14B variant needs a 24 GB GPU; the mini needs 8 GB+ with CUDA visible inside WSL2. This
script does not drive that path: the WSL toolchain install and the apiApp placement are
untested from the Windows side. Inside your WSL distro, from the repo root:

  nl-assist-finetune/install-prereqs.sh          # dotnet 10, node 22, uv, jq, build tools
  . nl-assist-finetune/.prereqs-env.sh           # DOTNET_ROOT / PATH / PY313
  Fallen8__Durability__Volatile=true ASPNETCORE_URLS=http://localhost:5000 \
    dotnet run --project fallen-8-core-apiApp -c Release &     # the compile authority
  cd nl-assist-finetune
  rm -f dataset/train.jsonl                      # FORCE regeneration: run.sh reuses an existing
                                                 # dataset and nothing verifies its sourceHash
  npx tsx dataset-gen/generate.ts --check        # after generation: must say "in sync"
  PYTHON="$PY313" ./run.sh deps
  VARIANT=phi4-f8-mini PYTHON="$PY313" ./run.sh all

Then the phase-4 eval ("Evaluation" in README.md). Full detail: README.md phase 3.
'@
}

# --- main ----------------------------------------------------------------------------------------

$L = Resolve-Launcher $Launcher
switch ($Stage) {
    'Azure' { $job = 'Finetune' }
    'Run' { $job = 'Finetune' }
    'Eval' { $job = 'Eval' }
    default { $job = 'None' }
}
Invoke-Preflight $L $job

Write-Head 'preflight result'
$script:Checks | Format-Table -AutoSize | Out-String -Width 240 | Write-Host

# Only the scopes this stage actually depends on can block it: consolidating captures against a
# local apiApp does not care what origin has, or whether Azure is reachable.
switch ($Stage) {
    'Consolidate' { $blockingScopes = @('Tools') }
    'Local' { $blockingScopes = @() }
    default { $blockingScopes = @('Tools', 'Repo', 'Azure') }
}
$blocking = 0
foreach ($s in $blockingScopes) { $blocking += $script:FailsByScope[$s] }
$other = 0
foreach ($s in @('Tools', 'Repo', 'Azure')) { if ($blockingScopes -notcontains $s) { $other += $script:FailsByScope[$s] } }

if ($blocking -gt 0) {
    Write-Host "$blocking blocking problem(s) above - fix them before spending GPU hours." -ForegroundColor Red
    exit 1
}
if ($other -gt 0) {
    Write-Host "preflight clean for -Stage $Stage ($other FAIL(s) above belong to a scope this stage does not use)." -ForegroundColor Yellow
}
else {
    Write-Host 'preflight clean.' -ForegroundColor Green
}

switch ($Stage) {
    'Preflight' { Write-Note 'checks only. Re-run with -Stage Run to consolidate and launch.' }
    'Consolidate' { Invoke-Consolidate }
    'Azure' { Invoke-AzureRun $L }
    'Run' { Invoke-Consolidate; Invoke-AzureRun $L }
    'Eval' { Invoke-EvalRun $L }
    'Local' { Show-LocalPlan }
}
