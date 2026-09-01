# CAN clusters: what shaped the design

Evidence and reasoning for the feature specified in [spec.md](./spec.md).

## What the reader was doing wrong

It knew two element names, `FLEXRAY-CLUSTER` and `FLEXRAY-FRAME`. An operator staging a set of
extracts whose buses were mostly CAN was told the set carried no FlexRay cluster, which was true
and useless.

## Two defects that were shipping, found before adding the capability

1. **A re-declared reference path meant "skip the subtree"**, on the premise that a twin is a repeat
   of the standard's shared catalogue. That premise fails across buses. An ECU's declaration is
   bus-local: a gateway on two buses appears in the extract of each, carrying only that bus's
   communication connector, so skipping the second declaration discards the second attachment. The
   two CONTAINER collectors now UNION their children while the element itself still belongs to the
   first file. Leaf collectors keep the old rule, because their remaining work records references
   under the same path and repeating it would hand them to the surviving twin. Every side-table
   write is guarded first-wins and every relation deduped, or the union would have made the graph
   depend on the order the caller listed the files.

2. **A job mixing readable and unreadable buses declared a COMPLETE snapshot over a half-read
   network.** It now imports what it can and names what it skipped, with the consequence stated in
   the diagnostic: complete over what was read means a later job omitting those files withdraws
   whatever only they described.

## Why CAN was cheap and Ethernet is not

CAN needed a protocol table rather than a second cluster walk: the cluster, channel, frame and
frame-triggering element names, the protocol value, and a triggering payload. Everything else is
shared verbatim, because a CAN frame carries the same children, the ECU connector predicate was
already a suffix match, and every PDU, signal, compu-method and reference-resolution path was
already protocol-neutral. No new entity kind, no new relation type, no new claim type, no new
setting.

A table and not a copy, because an **Ethernet cluster has no frame layer at all**. The standard's
Ethernet channels carry PDU triggerings directly, with the socket layer doing what a frame does
elsewhere, so a third copy of the cluster walk is how a third protocol becomes unaffordable.

## The per-job ceiling

`Integrations:MaxJobFileBytes` rose to 560 MiB. A multi-bus vehicle's extracts have to travel in ONE
job, because the snapshot is complete over what it was given and a smaller job withdraws the
difference. It could not rise further at the time: the JSON transport arm expands a job by 4/3, so
the largest job that arm can deliver inside the proxy's fixed 768 MiB budget is 575.25 MiB decoded,
and a higher ceiling would have the runtime accept jobs the proxy refuses with a bare 413.

## Also fixed

A static field initialiser read the protocol table through a helper before the table was assigned,
which the compiler cannot see through and which surfaced as a `NullReferenceException` from a type
initialiser on first use, failing every read rather than only the new protocol's. Every derived
static is now built in an explicit static constructor, so reordering a declaration cannot
reintroduce it.
