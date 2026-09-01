# Integration file upload - plan

Phases are ordered so the build is green at the end of each one. The runtime contract moves first,
because everything above it reads from the descriptor.

## P1 - The contract

- `Contract/ProviderDescriptor.cs`: add `SettingKind.File = 5`, documented the way `Credential` is -
  what it means, where the value arrives, and what a form does with it.
- `Contract/ProviderCatalog.cs`: if it validates settings, make sure a `File` setting with a
  `defaultValue` is refused (a default file name means nothing once nothing is opened by name).
- `Credentials/ProviderFileStores.cs`: `IProviderFileStore` keeps its two methods. `DirectoryFileStore`
  is **deleted**. A new `JobFileStore` serves the job's decoded payloads by setting key and throws
  once the run has ended, mirroring `CredentialLease`.
- Delete `Credentials/RootedNames.cs` - its only caller was the directory store.

## P2 - The job and the runner

- `Run/IntegrationJob.cs`: add `files` (`IDictionary<String, JobFile>`, `JobFile` = `name` +
  `contentBase64`). Fold it case-insensitively in `TryNormalize`, reject case-colliding keys, reject a
  missing/blank `contentBase64`, reject base64 that does not decode, and reject a decoded size over
  the ceiling. Carry the decoded bytes on `NormalizedJob`.
- `Run/JobRunner.cs`:
  - `EffectiveSettings`: a `File`-kind setting is satisfied by a `files` entry, never by a `settings`
    entry; a `File` key present in `settings` is a `JobRejectedException`; a required `File` setting
    with no `files` entry is a `JobRejectedException` naming it. The setting's effective VALUE becomes
    the supplied file's `name`, so `context.Required("file")` and every diagnostic subject keep
    working unchanged.
  - Build the `JobFileStore` beside the credential lease, inside the same `using` scope, and route
    `ReadFileAsync`/`ResolveFile` through it. Decode bytes to text with BOM detection so the result is
    identical to what `File.ReadAllTextAsync` produced.
  - Drop the injected `IProviderFileStore` singleton from the constructor.
- `Configuration/IntegrationsOptions.cs`: delete `FilesDirectory`, add `MaxFileBytes` (default
  33_554_432).
- `Hosting/IntegrationsHost.cs`: drop the `IProviderFileStore` registration and the startup log line
  naming the files directory; raise Kestrel's max request body size to sit above the ceiling.

## P3 - Descriptors

- `Providers/CsvDeviceList/CsvDeviceListProvider.cs` and
  `Providers/AutosarArxml/AutosarArxmlProvider.cs`: descriptor only - `Kind = SettingKind.File` and
  honest help text. **No `ObserveAsync` change in either.** If either needs one, the contract boundary
  was wrong and that is the finding, not a fix.

## P4 - The REST proxy

- `Controllers/IntegrationsController.cs`: a transport bound on `POST /integrations/job` sized for
  base64 inflation above the runtime ceiling, and remarks that describe how a file arrives and that it
  is dropped when the run ends.
- `Integrations/IntegrationsClient.cs`: check the forward path for a body-size or timeout assumption
  that a 32 MiB job would trip.

## P5 - Studio

- `api/types.ts`: `SettingKind` gains `"File"`; `IntegrationJobRequest` gains `files`.
- `screens/IntegrationsScreen.tsx`: a `File` arm on `SettingField` - dropzone plus picker, staged file
  name and size, replace affordance - reusing the Knowledge screen's drag handling. `buildJob` reads
  the staged files, base64-encodes them and puts them in `files`, never in `settings`. Staged files are
  cleared on provider switch and after a reported run, beside the credential clearing.
- Tests: the existing integrations screen tests, plus new ones for staging, submitting, and a required
  file blocking the run button.

## P6 - Conformance

- `Conformance/ConformanceVerifier.cs`: delete the path-escape check and its finding; keep
  `RunsOffline`'s file-read assertion. Add a check that a candidate declaring a `File` setting reads it
  through the context (so a provider that opened a path itself would be caught).
- The deliberately-broken candidates in the test suite: retire the one that only existed to fail the
  path-escape check, or repoint it at the new check.

## P7 - Tests

New tests (MSTest), each pinning one failure that is invisible from the graph:

1. A `File` setting satisfied by inline content runs, and the provider sees the exact text.
2. A UTF-16-with-BOM payload decodes to the same text as its UTF-8 twin.
3. A required `File` setting with no `files` entry is rejected, naming the setting.
4. A `File` key in `settings` is rejected, saying a file's content never arrives there.
5. A `files` entry for a key the provider does not declare is rejected.
6. A `files` entry for a non-`File`-kind setting is rejected.
7. Two `files` keys differing only in case are rejected.
8. `contentBase64` that is absent, blank, or not valid base64 is rejected, each with its own message.
9. A decoded payload one byte over `MaxFileBytes` is rejected, naming both sizes.
10. Reading the store after the run ends throws (FR-7).
11. The effective setting value is the supplied file `name`, so a diagnostic subject still names it.
12. Deleted: every `RootedNames` test in `IntegrationsCredentialTest.cs`, and the credential half of
    that file is left intact.

## P8 - Docs, compose, snapshots

- `docs/src/content/docs/integrations.md`: the table rows for both file providers, the "Running one"
  curl example, the ARXML section's "named by the `file` setting ... and the runtime opens it", and the
  conformance-check list (twelve named properties becomes whatever it is after P6). Add the upload
  story where the credential story is told, because they are now the same story.
- `docker-compose.yml`: delete the `./samples:/files:ro` mount and its comment; check
  `docker-compose.split.yml` and the runtime `Dockerfile` for the same.
- `features/done/integrations/spec.md` / `plan.md`: they are historical records and are NOT rewritten;
  the living doc is the docs-site page.
- Root `README.md`: the integrations line, if it mentions a file mount.
- Regenerate `scripts/update-provider-descriptor-snapshot.ps1` and
  `scripts/update-openapi-snapshot.ps1`, then **recapture `screen-integrations.png`** - a descriptor
  change means recapturing it, which is why that gate exists.

## Gates

```
dotnet build fallen-8-core.sln
dotnet test  fallen-8-core.sln --filter "FullyQualifiedName~Integrations"
dotnet test  fallen-8-core.sln
powershell -File scripts/update-provider-descriptor-snapshot.ps1
powershell -File scripts/update-openapi-snapshot.ps1
npm --prefix fallen-8-web-ui run test && npm --prefix fallen-8-web-ui run typecheck
npm --prefix docs ci && npm --prefix docs run build
```

The browser-wasm probe is **not** implicated: nothing here touches `fallen-8-core`.
