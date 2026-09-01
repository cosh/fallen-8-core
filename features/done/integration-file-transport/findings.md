# Job file transport: what shaped the design

## The problem

A job's files reached the runtime as base64 inside the job document. That is correct, and it costs a
third more bytes on the wire than the files themselves, and it obliges both the browser and the
runtime to hold the encoded form as well as the decoded one.

## What was built

A multipart arm beside the JSON one. The runtime reads parts with a streaming reader rather than the
form reader, because the form reader spools any part over a threshold to a temporary file, which
would falsify the runtime's no-disk contract. The browser submits multipart only, streamed from the
file handle, so it never holds an encoded copy.

## The bounds, and why they are where they are

- The apiApp proxy has a **fixed** job transport budget of 768 MiB, with 1 MiB reserved for the
  envelope, leaving about 767 MiB for files. It is a private const with no configuration key,
  deliberately: a caller must not be able to decide how much memory the process spends.
- The runtime's own Kestrel body bound sits ABOVE the proxy's, at 832 MiB, so an absurd body is
  refused at the front door rather than failing mid-forward and surfacing as a 503.
- The JSON arm expands a job by 4/3, so the largest job it can deliver inside the budget is
  575.25 MiB decoded. That, and not the configured ceiling, is what bounds `MaxJobFileBytes`.
- `MaxFileBytes` (128 MiB), `MaxJobFileBytes` and `MaxJobFiles` (256) belong to the runtime
  operator. The files-per-job bound exists because the byte ceilings cannot express it: a one-byte
  file is legal.

## A defect found by review

`MultipartReader` throws on a malformed body and nothing caught it, so a body the route could not
parse surfaced as an empty 500 rather than the caller's 400. A narrow framing guard now catches only
the framing exceptions, deliberately not cancellation and not everything, and the boundary length is
validated up front rather than discovered by a throw.

## Memory, which is the real constraint

A file's bytes are held for the whole run, and one whole file is decoded to a UTF-16 string, so peak
memory tracks the largest single file rather than the byte total. A streaming file read is the
deferral this feature names as the prerequisite for anything much larger.
