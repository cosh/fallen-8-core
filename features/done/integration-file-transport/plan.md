# Integration file transport: implementation plan

> Spec: [spec.md](./spec.md). Evidence: [findings.md](./findings.md). One branch,
> `feature/integration-file-transport`, all phases (owner decision).
>
> Each phase builds, leaves the suite green, and carries the documentation sentences it makes
> true. The screenshot is recaptured once, in the only phase that changes pixels.

## Worktree setup

`node_modules` is not checked out here. Junction it from the main checkout rather than
reinstalling, for both `fallen-8-web-ui` and `docs`, and remove junctions later with
`[System.IO.Directory]::Delete(path)` and never `Remove-Item -Recurse`, which follows the junction
into the real store. Confirm JS gate exit codes through
`cmd /v:on /c "<cmd> & echo EXIT=!ERRORLEVEL!"`; the PowerShell wrapper reports them unreliably.

**Do not run `npx prettier`.** This repo has no prettier config, so it reformats a whole file to a
foreign 80-column style. Edit source with the editing tools only, never by round-tripping a file
through PowerShell (`Get-Content`/`Set-Content` mojibakes every non-ASCII character and adds a BOM
while still building green).

## Gates

**Every phase touching .NET:** `dotnet build fallen-8-core.sln` (warnings are errors) and
`dotnet test fallen-8-core.sln` at normal verbosity, never `-v q`, which hides the failing test
name and makes a flake unidentifiable.

**Every phase touching Studio:** `npx tsc -b` and `npx vitest run` in `fallen-8-web-ui`.

**`tools/browser-probe` is not required by any phase:** nothing in `fallen-8-core` changes.

## Phases

**P0 - verify, no product change.** Re-run the live matrix against a real apiApp plus runtime and
append to `findings.md`: the current 415 on a multipart job (the `[Consumes]` proof), and the
open question the synthetic probe raised, namely whether an undrained over-limit body delivers a
status or resets on this machine. The 503-on-over-bound and both runtime 400s are already
recorded. Gate: `findings.md` updated with observed statuses and bodies.

**P1 - FR-9. Closes B2 (the false title).** `ErrorBox` plus a new `tests/error-box.test.tsx`: one
test per arm, and the load-bearing negative, that an `Error` about staged files is not titled
"Request failed", including the `RangeError: Invalid string length` shape the incident produced.
One file of product change, app-wide benefit, no contract. First because that false title cost
real diagnosis time. Gates: vitest, tsc.

**P2 - FR-5. Closes B3 (the unproducible 413).** The proxy `Content-Length` pre-check (413/411),
the per-request bound lowered to the declared length, `IntegrationsRequestRejectedException` and
its classification ahead of the existing `HttpRequestException` catch, `JobTimeoutSeconds` with
per-call linked cancellation instead of `HttpClient.Timeout`, and the setting catalogued or
`SettingCatalogTest` fails. Docs: `security.mdx`. Tests: 413 rather than 503 against a factory
pointed at a **closed** port, which is what makes it a real assertion; 411 for a chunked body; and
the regression that a 413 body does not contain "Integration runtime unavailable". Gates: full
`dotnet test`, `powershell -File scripts/update-openapi-snapshot.ps1` with the diff reviewed, and
the live 413/411 curls recorded in `findings.md`.

**P3 - FR-4 and FR-6. Closes B4 (no limits channel).** The runtime limits route, `MaxJobFiles`,
the composing proxy route, the `FileLimits` type, `useIntegrationLimits`, and `lib/fileLimits.ts`
as the single accessor. No form behaviour yet, so the screenshot is unchanged. `formatBytes` is
consolidated into `src/lib/format.ts` here rather than in P5 as first planned, deleting the
pre-existing duplicate in two screens: the accessor's refusals name sizes, and a third copy of the
formatter to be merged later is exactly the duplication this repo does not keep. The consolidated
one is the GiB-capable version, so the integrations file list stops rendering a multi-gigabyte set
as thousands of MiB. Tests: a defaults
test pinning 128 MiB / 512 MiB / 256 so a changed default fails a test rather than quietly staling
the published screenshot; the route reports configured numbers rather than constants; the proxy
lowers a runtime number above `JobTransportLimit - 1 MiB` and substitutes for `0`; an
`api-contract` entry pinning the path as Fallen-8-level and never namespace-prefixed. Gates:
`dotnet test`, OpenAPI snapshot, `McpRestCoverageTest`, `McpContractTest`, vitest, tsc.

**P4 - FR-1, FR-2, FR-3. The wire, server side.** `JobRequestReader` (whose type comment is the
one home for "how a job arrives, and why there are two ways"), `JobMultipartReader`, the `JobFile`
additions, the widened `[Consumes]`, the forwarded `Content-Length`, and the job-total message
naming the measured total. Includes the no-disk gate: a `CodeQualityTest` check that
`ReadFormAsync` / `IFormFile` / `IFormCollection` appear nowhere in `fallen-8-integrations`
product code, plus a behavioural test that a job whose file exceeds 64 KiB leaves an empty
per-test temp directory.

Tests, in the order they earn their keep: the both-transports identical-job anchor; bytes verbatim
with a `0x00` and a UTF-16 BOM; `filename` including `filename*` and a quoted name with a space;
ordinal order preserved; out-of-order, gap and repeated-ordinal refusals as three distinct
messages; the `AsList` fidelity pair (single form accepted by a non-`multiple` setting **and**
`[0]` refused by it), which must fail if anyone simplifies the grammar to repeated names; mixed
forms; single form twice; unknown part name; missing `filename`; a bracket in a key; the `job`
part absent, late, doubled, oversized, or carrying `files`; malformed JSON on both arms; 415;
case-colliding keys, duplicate names and empty parts asserted **on the message string** so it is
proven the same checks are reached; ceiling refusals from multipart including the `Truncated`
phrasing and a file at exactly the ceiling; `MaxJobFiles` on both transports; credential
redaction on the new path; and the proxy tests (multipart is not 415, the boundary survives
verbatim, `Content-Length` is forwarded, a JSON job is unchanged).

Two of these must run against **real Kestrel** on `127.0.0.1:0` rather than TestServer or they are
false greens: the header-time refusal, and "a refusal mid-body still reaches the caller". If the
second does not survive the hop, the fallback is a stated bounded drain allowance, not a hope.

Gates: `dotnet test`, OpenAPI snapshot, the two new gates above, plus a live `curl -F` run of the
ordering and both-ceiling cases.

**P5 - FR-7. Closes B5 (bytes never released).** `StagedFile` holds handles; `readBytes` and
`base64Of` deleted; `apiUpload` with determinate progress and cancel; the four-stage button
and upload row; `submitIntegrationJob` building the `FormData`. Tests: assert the probe read is one
byte (restoring `readBytes` must fail it); `expect(parts[0].file).toBe(theStagedFile)`, which
proves no copy and no re-encode; `btoa` never called across a three-file submit; the ordered-set
part assertions; the four progress stages over a deferred; the watch arms only once the runtime
accepted; cancel leaves nothing running; the send-failed copy. `tests/api-contract.test.ts` needs
an `XMLHttpRequest` stub in its harness or its completeness sweep fails with "submitIntegrationJob
issued no request", plus the multipart shape assertion that catches the Blob trap: `form.get("job")`
must be a **string**, since appending a Blob names the part `blob` and the server would bind the
envelope as a file. Gates: vitest, tsc, `npm run build:apiapp`.

**P6 - FR-8 and FR-10. Closes B1 (no early refusal).** The three staging refusals, the ceiling copy
on the file field and the total row, the stale-ceiling note, the limits stub in
`screenshot-integrations.spec.ts`, and the recapture. Sizes in tests are spoofed with
`Object.defineProperty(file, "size", ...)` so the multi-gibibyte scenario runs in microseconds. Include
the pin against a hardcoded fallback: with the limits query rejecting, a 700 MiB spoofed file
stages and the unknown-limits note renders.

Capture procedure is fixed by prior pain: build the SPA, run an app with **no**
`Fallen8__Security__ApiKey` (this spec stubs the sidecar and deep-links, so a keyed app replaces
the screen with a credential rejection and the capture times out), point Playwright at it with
`F8_UI_URL=http://127.0.0.1:<port>` rather than letting the config's `webServer` manage :5000,
never pipe the background launch through `Select-Object` (it closes the pipe and kills the app),
and then **look at the PNG**: a spec can pass while photographing the first-run scrim. Docs:
`integrations.md`, `studio.md`, and a `curl -F` example beside the base64 one. Gates: vitest, tsc,
the e2e capture, and `npm --prefix docs run build` (link-checked).

**P7 - sweep and record.** The spec's "Impact on existing features" confirmed against the tree;
the quote-and-overturn of `integration-file-upload/spec.md:120-121` with the identical-job test
named as the payment; the one-line lesson against `integration-run-lifecycle/spec.md` FR-5; the
README key-features line reviewed (its "held for one run and never stored" claim stays true);
`grep` for the two forbidden brand strings; a RETRAIN-LOG entry only if an NL-assist asset mentions
the transport. Then move this directory to `features/done/`. Gates: full `dotnet test`, docs build,
both snapshot scripts clean.

## Review

The council gate runs after P7, before merge. Code never lands on `main` directly. No GitHub issue
or PR unless asked.
