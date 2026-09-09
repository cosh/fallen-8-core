# Plan: Nahil is the default chat backend, and an unservable model says so

Companion to [spec.md](./spec.md). Branch `feature/nahil-default-backend` (branch-only workflow).

Two slices, in this order, because the second is only worth shipping once the first makes it
matter and the first is only honest once the second lands. Both are small; both land with tests.

## Slice 1: the refusal is immediate

Intent: a `503` that says no worker serves the model reaches the caller at once, with the
provider's own sentence.

- [x] `RetryAfterHandler`: give the provider hook a say over the RESPONSE, not just the status.
  Today `ShouldRetry(HttpStatusCode)` sees only the status, so a body-based discriminator has
  nowhere to live. Add a bounded body read and a per-provider `TryRefuse` seam that can turn a
  retryable status into an immediate failure carrying its own message. Keep the existing status
  path exactly as it is for everything that does not match.
- [x] `NahilWarmupRetryHandler`: implement that seam for the one documented case, matching
  `no attached worker serves` case-insensitively in a bounded prefix of the body, and raise a
  backend refusal whose message quotes the provider's sentence.
- [x] Make sure the body read cannot break the retry path: read once, bound the length, and leave
  the response usable (or dispose it deliberately) on both branches.
- [x] Map the new failure to a status: it is the backend refusing, so it travels as the
  provider's own failure to the chat and embedding providers rather than as a
  `ModelRetryTimeoutException`. Check both providers' exception mapping and pick the arm that
  already means "the backend refused", so no caller learns a new exception type.
- [x] Tests (`RetryAfterHandlerTest` / the Nahil handler's tests, with the injected delay so no
  test sleeps): the no-worker body fails on the FIRST attempt with zero waits and the sentence in
  the message; a `503` with `Retry-After` still waits and retries; a `503` with an unrelated body
  still waits and retries; a `429` is unchanged; a body larger than the bound is still classified
  correctly if the phrase is inside the bound and retried if it is not.
- [x] Commit. Two commits: the fix, then a second one bounding the refusal tests, because a
  mutation check found they HUNG rather than failed when the fix was disabled.

## Slice 2: the default flips

Intent: an instance told nothing about its chat backend resolves to Nahil and refuses with a name.

- [x] `Fallen8ChatOptions.Backend`: `"Ollama"` becomes `"Nahil"`; rewrite the doc comment to state
  the fail-closed reason (no default endpoint by construction, so a missing block is named rather
  than guessed) instead of naming a favourite.
- [x] `docker-compose.yml`: **the line was already there.** This plan assumed it had to be added;
  the base file has pinned both backends all along, so the flip changes nothing in compose. Only
  a comment was added, saying the line is now load-bearing rather than tidy.
- [x] ENTAILED, and not in this plan when it was written: `Fallen8:Chat:Nahil:Endpoint` and
  `Model` gained defaults so the refusal names the credential, and
  `Fallen8:Chat:TimeoutSeconds` moved 120 to 600 because the default backend warms up inside
  that budget. Both are argued in the spec.
- [x] Check every other place that could inherit the old default: `appsettings*.json` (neither
  sets a Chat section today), the split/gpu/observability overlays, the CI smoke-test environment
  in `.github/workflows/`, `launchSettings.json`, and the Dockerfile posture comments. Fix or
  confirm each.
- [x] `McpBridgeTest.Overview_WithNamespace_ReturnsThatGraphsStatus`: expect `Nahil` and adjust the
  comment to say what the shipped default now tells an agent.
- [x] Run the full suite and fix every other test that depended on the default rather than setting
  it explicitly (the audit found only the one above; the suite is the check).
- [x] Docs: correct the default in `model-providers.md`, `nahil.md` (which currently says the local
  sidecar stays the default) and `running.mdx`; check `configuration.md`, `nl-assist.md`,
  `troubleshooting.md` and `security.mdx` for the same claim.
- [x] Amend `features/done/nahil-backend/spec.md` decision 2 and the matching sentence in
  `features/done/model-providers/spec.md` with a dated note, original sentence left standing.
- [x] Commit.

## Slice 3: land

- [x] Full suite green (2523 passed), build clean, docs site builds with valid links, compose
  parses and resolves as intended, no banned word and no em dash in anything added.
- [x] Screenshots: **not needed**, checked rather than assumed. The capture app wires
  `Fallen8__Chat__*` explicitly, so the frames are unchanged; the connect guard's failure message
  now says the backend must be named explicitly.
- [ ] Merge to `main`, move `features/open/nahil-default-backend/` to `features/done/`.
