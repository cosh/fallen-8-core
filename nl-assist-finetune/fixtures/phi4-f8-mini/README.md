# `phi4-f8-mini:latest` - what the local build actually was

Captured 2026-08-22 from the `f8-ollama-models` volume with `ollama show`. This exists because the
local image is the only copy of that build on this machine: nothing here reproduces it
byte-for-byte, and once the volume is gone the three things a caller's output actually depends on -
the system prompt, the prompt template and the sampling parameters - would be gone with it.

| File | Captured with |
| --- | --- |
| [`system.txt`](system.txt) | `ollama show --system phi4-f8-mini:latest` |
| [`template.jinja`](template.jinja) | `ollama show --template phi4-f8-mini:latest` |
| [`parameters.txt`](parameters.txt) | `ollama show --parameters phi4-f8-mini:latest` |
| [`show.txt`](show.txt) | `ollama show phi4-f8-mini:latest` (architecture, quantization, capabilities) |

Verbatim, including the absence of a trailing newline on `template.jinja`: the model carries no
template layer of its own, so ollama renders it from the GGUF's own chat template, and that is what
the file holds.

## Which build this is, exactly

The local tag `phi4-f8-mini:latest` holds the content of the **pre-rename published repo**,
`stoic_hellman_728/f8-delegate`. Read off the volume's manifests:

```
library/phi4-f8-mini:latest            model sha256:3ab5bf48b74b…7e58fef0
library/f8-delegate:latest             model sha256:3ab5bf48b74b…7e58fef0   same
stoic_hellman_728/f8-delegate:latest   model sha256:3ab5bf48b74b…7e58fef0   same - the source
stoic_hellman_728/phi4-f8-mini         ABSENT - never pulled onto this machine
```

Full digests: config `e0be8b67…88b7a`, model `3ab5bf48…8fef0` (2,493,840,256 B), system
`4b90ddaa…af4d56` (361 B), params `eb4ca979…8fd7f8` (20 B). `ollama list` reports one id,
`6d4bd13b1fda`, for `phi4-f8-mini:latest` and `f8-delegate:latest` alike, and `--system`,
`--template` and `--parameters` are byte-identical between them.

`f8-delegate` was the fine-tune's original name; [`delegate-model-variants`](../../../features/done/delegate-model-variants/)
renamed it to `phi4-f8-mini` with **no local alias**, deliberately. The `f8-delegate` tags in this
volume are residue from before that rename, not an alias anyone should point at locally - and they
are the reason the two names agree here.

## Why that matters off this machine

On [Nahil](https://docs.fallen-8.com/nahil/) the two names do **not** agree:

| Name on Nahil | What it serves |
| --- | --- |
| `phi4-f8-mini:latest` | the published `phi4-f8-mini` repo - a **later build**, which this machine has never held |
| `f8-delegate:latest` | the published `f8-delegate` repo - **this** build, the one described here |

So a deployment that wants exactly the output this fixture describes sets
`F8_NAHIL_CHAT_MODEL=f8-delegate:latest`; one that wants the current published finetune leaves the
default. Neither is more correct - they are different weights - and this file is how you tell which
one you are talking to. It also creates no alias: it names a different catalog entry on a remote
backend, which leaves the clean-rename decision intact.

## Not a test fixture

Nothing asserts against these files. They are a record, so a future rebuild or a divergence between
the two Nahil names can be diagnosed rather than argued about.
