# MIT License
#
# update-provider-descriptor-snapshot.ps1
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

# Regenerates the pinned provider-descriptor snapshot
# (features/done/integrations/provider-descriptors.json) - what GET /integration/providers serves,
# read by ProviderDescriptorSnapshotTest and served as the stub by the Integrations docs screenshot
# capture. Unlike the OpenAPI snapshot no port is taken: the test hosts the runtime in process, so
# the update flag hands the writing to it.
# Usage: powershell -File scripts/update-provider-descriptor-snapshot.ps1   (from anywhere)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$snapshot = Join-Path $root "features/done/integrations/provider-descriptors.json"

$old = $env:F8_UPDATE_PROVIDER_DESCRIPTOR_SNAPSHOT
$env:F8_UPDATE_PROVIDER_DESCRIPTOR_SNAPSHOT = "1"
try {
    # The update run reports inconclusive rather than passing: in update mode nothing is judged.
    dotnet test (Join-Path $root "fallen-8-core.sln") -v q --nologo `
        --filter "FullyQualifiedName~ProviderDescriptorSnapshotTest.ServedDescriptors_MatchTheCommittedSnapshot"
}
finally {
    $exit = $LASTEXITCODE
    $env:F8_UPDATE_PROVIDER_DESCRIPTOR_SNAPSHOT = $old
}

# A non-zero run means the test failed BEFORE it could write, so the file on disk is still the old
# one - say so rather than announcing a snapshot that was not taken.
if ($exit -ne 0) { throw "the update run failed; $snapshot is unchanged" }

Write-Host "snapshot written: $snapshot"

# Review the printed diff. A shipped descriptor that changed here changes the published Integrations
# screenshot too, so recapture it:
#   cd fallen-8-web-ui; $env:F8_SCREENSHOT="1"; npx playwright test e2e/screenshot-integrations.spec.ts
git -C $root diff --stat -- $snapshot
