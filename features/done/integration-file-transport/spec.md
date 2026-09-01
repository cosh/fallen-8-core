# An integration job's files travel as bytes

**Status: IMPLEMENTED.**

## What it does

A job may carry its files as `multipart/form-data` instead of base64 inside the job document. The
instance publishes what it will accept, so a client can refuse an oversized set before uploading it
rather than after.

## Contract

- The job document travels as one part named `job`; every other part is a file, addressed by the
  setting it belongs to.
- The JSON arm keeps working unchanged. A provider cannot tell which arm a file arrived by: the
  effective value of a file setting is the file's own NAME either way, so a provider never sees a
  path and nothing is mounted.
- Parts are read with a streaming reader, never the form reader, because the form reader spools to a
  temporary file and this runtime's contract is that a file's bytes never touch disk.
- A body the route cannot parse is the caller's 400 with a reason, never an empty 500.
- `GET /integrations/limits` reports the per-file, per-job and files-per-job ceilings already
  reconciled with the proxy's fixed transport budget, so a client applies one set of numbers rather
  than guessing which bound will bite first.
- A declared `Content-Length` over the budget is refused before the upload begins, and a request
  with no declared length is refused outright.

## Impact on existing features

The engine is untouched. Studio submits multipart. The OpenAPI snapshot gains the route and the
limits shape. Every `/integrations/*` route remains deferred rather than bridged to MCP, for the
reason recorded there: a job run is a complete-snapshot write that no unverifiable identity may
trigger.
