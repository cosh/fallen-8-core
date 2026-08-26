# Embeddable Studio - living notes

The contract, the packaging and the CI tripwires are specified in [spec.md](spec.md) and
[plan.md](plan.md), which are the historical record of how this landed. The user-facing page
is <https://docs.fallen-8.com/embed-studio/>. This file carries only what is still LIVE.

## Known issue: a modal's scrim escapes the region the host allotted

Studio's shared modal classes (`.modal-overlay` and `.modal-center`, both scoped under
`:where(.f8-studio)` in `fallen-8-web-ui/src/index.css:91-97`) are `position: fixed`. In the SPA
that is correct, because the
viewport IS the app. In an EMBED it is not: the scrim covers the whole host page rather than the
`#studio-region` the host gave Studio, so a Studio dialog can intercept clicks on the host's own
chrome.

Found on 2026-08-26 while fixing the embed smoke, which had been red because the first-run
walkthrough's scrim swallowed every click after a navigation. That symptom is fixed in the test
(the suite now dismisses the walkthrough, as an operator must), but the underlying containment
question is untouched, because it affects EVERY Studio dialog and both host modes, and changing
`fixed` to `absolute` within the scope root is a design decision about what an embedded modal is
allowed to cover.

**Revisit trigger:** a host reports that Studio blocked its own UI, or the embed grows a dialog a
host is expected to interact around. Whoever takes it should decide the rule once, on the shared
modal classes, rather than per dialog.

Related, on the walkthrough's own side rather than the embed's:
[features/done/studio-first-run/README.md](../studio-first-run/README.md).
