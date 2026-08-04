# Sample documents

The documents the **Wind Farm Fleet Integrity** gallery sample ingests at load time (feature
`knowledge-demo`). They are fictional, English, and written to exercise one converter path each:

| File | Path exercised | What it is |
| --- | --- | --- |
| `nw-rca-wtg-a17.pdf` | docling PDF, carries a raster figure | A root-cause analysis explaining why a gearbox bearing failed |
| `nw-fleet-register.xlsx` | docling XLSX, becomes `kind: table` chunks | The maintenance register, plus a prose notes sheet |
| `nw-std-0417.md` | no sidecar at all | An engineering standard explaining why the alarm limit sits where it does |

All three are **reproducible**: regenerating with unchanged inputs produces byte-identical files,
so `git status` stays clean. That needs explicit work in two places (reportlab's `invariant=1`, and
rewriting the XLSX zip's member timestamps plus the `dcterms:modified` that openpyxl stamps at
save), which is why the generator does it rather than leaving the binaries to churn on every run.

## Why these are committed rather than built

Authoring them needs a PDF and spreadsheet writer, and the sample build
(`npm run build:samples`) is `vite-node` TypeScript. Adding that toolchain to the build just to
re-derive three static files on every run is bloat, and the repo already pins other sample
inputs (the stored SBOM, the curated movie list) instead of refetching them. So the outputs are
committed and `generate-documents.py` is run by hand when the content changes:

```bash
pip install reportlab openpyxl matplotlib
python samples/documents/generate-documents.py
```

## Constraints the content must respect

Each was verified against a live instance, and each would silently degrade the demo if broken.
The generator prints the RCA's and the standard's section lengths on each run so the first one
fails visibly. (The spreadsheet's Notes sheet is not measured; it is short by design and docling
emits it as its own table chunk regardless.)

- **Sections must exceed 800 characters.** Chunking merges anything shorter into its neighbour,
  which collapses a multi-section document into a single chunk and destroys the chunk chain.
- **Asset tags come from `windFarmFleet.json` and nowhere else.** That file is the single source
  of truth shared with the graph generator
  ([`fallen-8-web-ui/scripts/samples/windFarm.ts`](../../fallen-8-web-ui/scripts/samples/windFarm.ts)).
  Structural linking is ordinal-exact, so one character of drift links nothing at all.
- **A figure caption does not survive conversion.** docling attaches it to the picture, which the
  chunker does not model, so anything a figure means is stated in body prose as well.
- **The section a reader lands on must name the asset it discusses.** The mechanism section
  originally explained the failure without naming the unit, so the top hit for the obvious query
  linked to zero assets and the demo's central claim was false.
- **The register must not list the whole suspect batch.** The payoff is that the graph reveals
  batch members no document names; `registerRows` in the fleet file controls exactly that.
