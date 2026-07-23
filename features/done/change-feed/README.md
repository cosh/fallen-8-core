# Change Feed — Usage

A live stream of committed graph mutations over **Server-Sent Events**, with declarative
server-side filtering and a bounded in-memory catch-up buffer. Companion docs:
[spec.md](./spec.md) (contract) and [plan.md](./plan.md) (phases).

## The client recipe

**Fetch, then stream; on `resync`, re-fetch.** That is the whole contract:

1. Fetch the state you care about via the REST reads.
2. Open the stream (optionally filtered) and apply events as they arrive.
3. On ANY `resync` event, re-fetch what you display. For `reason` `trim`, `tabulaRasa` or
   `load`, additionally treat every element id you hold as invalid.

Delivery is in-order (commit order, strictly ascending `seq` per connection), at-most-once
per connection; whenever continuity is lost — a slow consumer, a buffer overrun, a server
restart, an out-of-range seek — the stream says so in-band with `resync` instead of silently
missing events. A slow consumer's owed `resync(overflow)` is delivered as soon as it frees
queue space (no further commit required) and carries the sequence number of the last event
it missed.

## Opening a stream

```bash
# Everything, live:
curl -N http://localhost:5000/changefeed

# Only person-vertex creations and removals:
curl -N "http://localhost:5000/changefeed?kinds=vertexCreated,vertexRemoved&labels=person"

# Catch up after a disconnect (id: value of the last event you saw):
curl -N "http://localhost:5000/changefeed?since=0b1e4c2e-...:4712"
```

Browser, unauthenticated (loopback developer default):

```js
const source = new EventSource("/changefeed?labels=person");
source.onmessage = (e) => console.log(JSON.parse(e.data));
// EventSource reconnects automatically and sends Last-Event-ID, which the server
// honours as `since` - catch-up is built in.
```

Authenticated (an API key is configured): `EventSource` cannot set headers, so consume the
same wire format with `fetch` + a stream reader — deliberately, **the key never goes into a
query string** (URLs leak into logs and proxies):

```js
const response = await fetch("/changefeed?labels=person", {
  headers: { "X-Api-Key": key },
});
const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
// parse "id:"/"event:"/"data:" lines; ": keepalive" comments are ignorable
```

## Event schema

One JSON object per `data:` line; fields are ABSENT when not applicable. Payloads carry ids,
labels and property **keys** only — never property values (re-fetch the element when you
need the value).

```jsonc
{
  "seq": 4712,                      // monotonic, commit order, gap-free per epoch
  "ts": "2026-07-15T12:34:56.789Z", // commit timestamp (UTC), shared by a transaction's events
  "kind": "propertySet",            // vertexCreated | vertexRemoved | edgeCreated | edgeRemoved
                                    // | propertySet | propertyRemoved | resync
  "element": "vertex",              // element events only
  "id": 42,                         // element events only
  "label": "person",                // omitted when unlabeled
  "key": "name",                    // property events only
  "source": 7, "target": 9,        // edgeCreated only
  "reason": "trim"                  // resync only: trim | tabulaRasa | load | delegateWrite
                                    //              | overflow | seekOutOfRange
}
```

## Filter grammar

All parameters optional (unset = wildcard), repeatable and/or comma-separated (union within
a dimension), matched case-sensitively; dimensions combine with AND:

| Parameter | Values | Notes |
|---|---|---|
| `kinds` | the kind names above | `resync` is accepted but always delivered anyway |
| `elements` | `vertex`, `edge` | |
| `labels` | exact labels | an unlabeled element never matches |
| `keys` | exact property keys | only property events carry a key, so this excludes creates/removes — subscribe twice to see both |
| `since` | `<epoch>:<seq>` or bare seq | replayed from the in-memory ring; out of window ⇒ leading `resync(seekOutOfRange)` |

Unknown values are a **400** problem+json, never a silently-empty stream. Filters are
declarative — the feed is fully functional with `EnableDynamicCodeExecution=false`.

## Configuration

```jsonc
"Fallen8": {
  "ChangeFeed": {
    "Enabled": true,             // false ⇒ endpoint answers 503, engine pays a null check
    "BufferSize": 8192,          // catch-up ring capacity (events)
    "SubscriberQueueSize": 1024, // per-subscriber bounded queue
    "MaxSubscribers": 32,        // beyond ⇒ 503 (per namespace — each has its own feed, see graph-namespaces)
    "KeepAliveSeconds": 15       // SSE comment heartbeat
  }
}
```

The endpoint sits behind the standard fallback auth policy (API key) like every other
endpoint.

## Throughput non-regression (measured)

`ChangeFeedThroughputBenchmark` (opt-in, `[TestCategory("Benchmark")]`), 20 000 committed
single-vertex transactions per round, three measured rounds after a warm-up, on a
WAL-enabled engine (2026-07-16):

| Configuration | median tx/s | rounds (min..max) |
|---|---|---|
| feed off | 100 405 | 87 097 .. 105 857 |
| feed on, no subscriber | 95 905 | 92 503 .. 102 501 |
| feed on, one draining subscriber | 97 221 | 85 027 .. 106 357 |
| feed on, stalled subscriber (overflow path) | 104 315 | 82 740 .. 111 234 |

The per-round ranges of all four configurations overlap heavily — the differences are
run-to-run noise (the stalled configuration posting the highest median makes that plain).
The writer-side cost is one descriptor capture plus one non-blocking channel write per
committed transaction; fan-out happens off the writer.

## Deployment note (proxies)

SSE is plain HTTP, but buffering middleware or proxy idle timeouts can stall or kill
long-lived streams. The server disables response buffering and heartbeats every
`KeepAliveSeconds`; if you front Fallen-8 with a proxy (the recommended TLS posture), turn
off response buffering for `/changefeed` (nginx: `proxy_buffering off;`) and set its read
timeout above the heartbeat interval.
