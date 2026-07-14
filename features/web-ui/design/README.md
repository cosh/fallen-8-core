# F8 Studio — design prototype

Interactive UI design for the [web-ui feature](../spec.md), authored in the
Claude Design project ["Fallen-8 web interface"](https://claude.ai/design/p/67c917e2-5514-4c03-9495-9353fd2616ab)
and pulled into the repo on 2026-07-14. The canonical, editable version lives in
that project; this folder is a byte-exact snapshot for review alongside the spec.

## Files

| File | What it is |
|------|------------|
| `F8 Studio.dc.html` | The prototype: all seven screens from the spec's UX surface (instances, dashboard, element browser, query workspace, subgraph studio, graph canvas, path finder) as declarative `<x-dc>` template markup. |
| `support.js` | The Claude Design runtime that renders the `<x-dc>` markup; loaded by the HTML via a relative path. |
| `assets/F8Black.svg`, `assets/F8White.svg` | Fallen-8 logo variants that were uploaded to the design project. Not referenced by the prototype markup — kept as brand assets for the implementation. |

## Viewing

Open `F8 Studio.dc.html` directly in a browser (keep it next to `support.js`).
Web fonts (Rajdhani, IBM Plex Sans, JetBrains Mono) load from Google Fonts, so
typography needs an internet connection; everything else is self-contained.
