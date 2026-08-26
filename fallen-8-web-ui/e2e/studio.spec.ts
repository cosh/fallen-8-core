// MIT License
//
// studio.spec.ts
//
// Copyright (c) 2011-2026 Henning Rauch
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

import { expect, test, type Page } from "@playwright/test";

/**
 * F8 Studio end-to-end scenarios (spec §9) against a live apiApp serving the built SPA.
 * The server runs with an API key ("e2e-key"); dynamic code execution is always on (see
 * playwright.config.ts), so every test first registers a same-origin instance carrying
 * that key through the real Connect screen.
 */

const API_KEY = process.env.F8_E2E_API_KEY ?? "e2e-key";

async function registerSecuredInstance(page: Page, name = "e2e") {
  await page.goto("/");
  await page.getByTestId("instance-add").click();
  await page.getByTestId("instance-name").fill(name);
  await page.getByTestId("instance-url").fill("");
  await page.getByLabel(/api key/i).fill(API_KEY);
  await page.getByTestId("instance-save").click();
  await page.getByRole("radio", { name: `activate ${name}` }).check();
  // The endpoint hint carries the namespace prefix (feature graph-namespaces).
  await expect(page.getByTestId("active-endpoint")).toContainText("same origin");
  // Then do what a newcomer does first: open a graph screen, meet the first-run walkthrough, and
  // put it away. It is MODAL and opens itself on any graph screen of an empty namespace, so
  // leaving it up would make every scenario below click at its scrim instead of its own screen.
  // The dismissal is per namespace, which is why scenario 12 repeats it for the one it creates.
  await dismissIntroOnGraphScreen(page, DEFAULT_NAMESPACE);
}

/** The reserved namespace every scenario starts in (bare URLs alias it). */
const DEFAULT_NAMESPACE = "default";

/**
 * Land on a graph screen of `namespace`, close the first-run walkthrough if it opened itself, and
 * come back to Connect - which is where the setup left the caller before the show existed, and what
 * the scenarios that go on to use the Connect panels rely on. The Browser is the cheapest graph
 * screen to detour through: it fetches nothing until asked.
 */
async function dismissIntroOnGraphScreen(page: Page, namespace: string) {
  await page.goto(`/q/${namespace}/browser`);
  await dismissFirstRunIfPresent(page);
  await page.goto("/");
}

/**
 * On an empty namespace the first-run walkthrough (feature studio-first-run) opens ITSELF as a
 * shell-level overlay, over whatever screen is showing. Scenarios that then interact with that
 * screen dismiss it first (Skip to the handoff, then Explore on my own, which records the
 * dismissal). A no-op when the graph is already populated, or on a fresh context where the
 * instance is not connected yet and the show therefore never appeared.
 */
async function dismissFirstRunIfPresent(page: Page) {
  const show = page.getByTestId("first-run-show");
  try {
    await show.waitFor({ state: "visible", timeout: 8_000 });
  } catch {
    return; // no show (populated graph), nothing to dismiss
  }
  const skip = page.getByTestId("first-run-skip");
  if (await skip.count()) await skip.click().catch(() => {});
  await page.getByTestId("first-run-explore").click();
  await show.waitFor({ state: "hidden" });
}

/**
 * The active namespace's vertex count, read where it lives now: the top bar's namespace switcher
 * ("<name> N v · M e", from GET /ns), which every screen carries. It used to be a Dashboard
 * tile, and the Dashboard was removed precisely because the top bar already said this. Returns NaN
 * while the inventory reports no counts, so callers poll.
 *
 * Anchored on the " v · " pair rather than on "digits then v": the cell text begins with the
 * NAMESPACE NAME, and a namespace called e.g. "q3-2026" would otherwise donate its digits to the
 * count. Namespace names may hold digits, so that is a real input, not a hypothetical.
 */
async function activeVertexCount(page: Page): Promise<number> {
  const text = (await page.getByTestId("namespace-switcher").textContent()) ?? "";
  const match = /(\d[\d,]*) v · /.exec(text);
  return match ? Number(match[1].replace(/\D/g, "")) : NaN;
}

/**
 * PUT /vertex returns 202 with no id, so the created vertex is found the way a user
 * finds it: create with a unique label, load the bulk view, read the id off its row.
 */
async function createVertex(page: Page, labelPrefix: string): Promise<number> {
  const label = `${labelPrefix}-${Date.now().toString(36)}-${Math.floor(Math.random() * 1e6)}`;
  await page.goto("/browser");
  await page.getByTestId("new-vertex-label").fill(label);
  await page.getByTestId("create-vertex").click();
  await expect(page.getByTestId("mutation-message")).toContainText(label);

  await page.locator("#max-elements").fill("5000");
  await page.getByRole("button", { name: "Load", exact: true }).click();
  await page.getByTestId("bulk-filter").fill(label);
  const row = page.locator("tr", { hasText: label }).first();
  await expect(row).toBeVisible({ timeout: 20_000 });
  const id = Number(await row.getByRole("button").first().textContent());
  expect(Number.isInteger(id)).toBe(true);
  return id;
}

test("scenario 1: connect, health, disconnected overview", async ({ page }) => {
  await registerSecuredInstance(page);

  // A dead endpoint is visible in the overview before switching to it (FR-1a).
  await page.getByTestId("instance-add").click();
  await page.getByTestId("instance-name").fill("dead");
  await page.getByTestId("instance-url").fill("http://localhost:59999");
  await page.getByTestId("instance-save").click();
  await expect(
    page.getByTestId("instance-row-dead").getByText("unreachable"),
  ).toBeVisible({ timeout: 20_000 });

  // A legacy /dashboard bookmark forwards to the Browser in the active namespace: the screen is
  // gone, its URL still answers rather than rendering a blank shell.
  await page.goto("/dashboard");
  await expect(page).toHaveURL(/\/q\/default\/browser$/);
  await dismissFirstRunIfPresent(page);
  await expect(page.getByTestId("health-chip")).toHaveText("online");
  // What the Dashboard's tiles said is in the top bar, on this screen and every other one.
  await expect(page.getByTestId("namespace-switcher")).toContainText(/\d+ v/);
});

test("scenario 2: the Benchmark tab generates a graph and shows structured numbers", async ({
  page,
}) => {
  await registerSecuredInstance(page);
  // The screen is namespace-scoped (generation WRITES the addressed graph), so the flat
  // "/benchmarks" is a legacy path that redirects to the active namespace's screen.
  await page.goto("/benchmarks");
  await expect(page).toHaveURL(/\/q\/default\/benchmarks/);
  await page.getByTestId("generate-sample").click();
  await expect(page.getByTestId("generate-result")).toBeVisible({ timeout: 30_000 });
  // The structured result names the namespace the SERVER wrote into, which is what regressed
  // when this screen was Fallen-8-level: every generation landed in "default".
  await expect(page.getByTestId("stat-into-namespace")).toHaveText("default");
  await expect(page.getByTestId("stat-vertices-created")).not.toHaveText("0");

  // The generated vertices show up in the top bar's live counts, on whatever screen we are on.
  await expect.poll(async () => activeVertexCount(page), { timeout: 30_000 }).toBeGreaterThan(0);

  // Structured benchmark output rendered as stat tiles, plus the session run history.
  await page.goto("/q/default/benchmarks");
  await page.getByTestId("run-benchmark").click();
  await expect(page.getByTestId("benchmark-result")).toBeVisible({ timeout: 120_000 });
  await expect(page.getByTestId("stat-avg-tps")).not.toHaveText("—");
  await expect(page.getByTestId("stat-edges-per-run")).not.toHaveText("0");
  await expect(page.getByTestId("benchmark-history")).toBeVisible();
});

test("scenario 3+4: mutate, browse, scan, hydrate, canvas", async ({ page }) => {
  await registerSecuredInstance(page);

  const source = await createVertex(page, "person");
  const target = await createVertex(page, "person");

  // Set a typed property, then look the vertex up (FR-5) and walk adjacency (FR-6).
  await page.getByTestId("mutation-tab-property").click();
  await page.locator("#mp-element").fill(String(source));
  await page.locator("#mp-id").fill("age");
  await page.getByLabel(/^value type$/).selectOption("System.Int32");
  await page.getByTestId("mp-value").fill("42");
  await page.getByRole("button", { name: "Set property" }).click();
  await expect(page.getByTestId("mutation-message")).toContainText("age");

  await page.getByTestId("mutation-tab-edge").click();
  await page.locator("#me-source").fill(String(source));
  await page.locator("#me-target").fill(String(target));
  await page.locator("#me-prop").fill("knows");
  await page.getByRole("button", { name: "Create edge" }).click();
  await expect(page.getByTestId("mutation-message")).toContainText("Edge");

  await page.getByTestId("lookup-id").fill(String(source));
  await page.getByTestId("lookup-go").click();
  await expect(page.getByRole("cell", { name: "age", exact: true })).toBeVisible();
  await expect(page.getByTestId("degrees")).toContainText("out 1");

  // Property scan (Equals, typed literal) -> id list hydrates -> table -> canvas (scenario 4).
  await page.goto("/query");
  await page.getByTestId("scan-property").fill("age");
  await page.locator("#scan-operator").selectOption("Equals");
  await page.getByLabel(/^literal type$/).selectOption("System.Int32");
  await page.getByTestId("scan-literal-value").fill("42");
  await page.getByTestId("scan-run").click();
  await expect(page.getByText(`results — 1 ids`)).toBeVisible({ timeout: 20_000 });
  await page.getByTestId("send-to-canvas").click();

  await page.goto("/canvas");
  await expect(page.getByText(/1 elements|2 elements/)).toBeVisible();

  // Style panel (studio-canvas-viz FR-6/FR-8): data-driven styling and the 3D projection
  // keep the same canvas contents; switching back restores the 2D renderer + new layouts.
  await page.getByLabel("size by").first().selectOption("degree");
  await page.getByLabel("color by").first().selectOption("property");
  await page.locator("#style-node-color-prop").fill("age");
  await page.getByTestId("style-renderer").selectOption("3d");
  await expect(page.getByTestId("graph-canvas")).toBeVisible();
  await expect(page.getByTestId("style-layout")).toHaveValue("force");
  await page.getByTestId("style-renderer").selectOption("2d");
  await page.getByTestId("style-layout").selectOption("grid");
  await expect(page.getByTestId("graph-canvas")).toBeVisible();
  await expect(page.getByText(/1 elements|2 elements/)).toBeVisible();
});

test("scenario 5: delegate editor validates, blocks, then passes and the path runs", async ({
  page,
}) => {
  await registerSecuredInstance(page);

  // Deterministic three-vertex chain via the UI.
  const a = await createVertex(page, "person");
  const b = await createVertex(page, "person");
  const c = await createVertex(page, "person");
  await page.getByTestId("mutation-tab-edge").click();
  for (const [s, t] of [
    [a, b],
    [b, c],
  ]) {
    await page.locator("#me-source").fill(String(s));
    await page.locator("#me-target").fill(String(t));
    await page.locator("#me-prop").fill("knows");
    await page.getByRole("button", { name: "Create edge" }).click();
    await expect(page.getByTestId("mutation-message")).toContainText("Edge");
  }

  await page.goto("/path");
  await page.getByTestId("path-from").fill(String(a));
  await page.getByTestId("path-to").fill(String(c));
  await page.getByTestId("toggle-advanced").click();
  await page.getByTestId("slot-filter-vertexfilter").click();

  // Type an unknown-member fragment into Monaco: marker + INVALID + blocked commit.
  const editor = page.locator(".monaco-editor").first();
  await editor.click();
  await page.keyboard.press("Control+a");
  await page.keyboard.type('return (v) => v.DoesNotExist;', { delay: 10 });
  await expect(page.getByTestId("validation-invalid")).toBeVisible({ timeout: 15_000 });
  await expect(page.getByTestId("commit-fragment")).toBeDisabled();

  // Fix it: VALID enables commit. (Labels carry a unique suffix, hence StartsWith.)
  await editor.click();
  await page.keyboard.press("Control+a");
  await page.keyboard.type('return (v) => v.Label.StartsWith("person");', { delay: 10 });
  await expect(page.getByTestId("validation-valid")).toBeVisible({ timeout: 15_000 });
  await page.getByTestId("commit-fragment").click();

  await page.getByTestId("path-run").click();
  await expect(page.getByText(/results — [1-9]\d* path/)).toBeVisible({ timeout: 20_000 });
  await expect(page.getByTestId("path-weight-0")).toHaveText("0"); // BLS ignores costs (scenario 6)
});

test("scenario 7: subgraph lifecycle with empty-as-valid", async ({ page }) => {
  await registerSecuredInstance(page);
  await page.goto("/subgraphs");

  await page.getByTestId("sg-name").fill("e2e-sub");
  // Alternation guard: two vertex steps in a row must block creation client-side.
  await page.getByTestId("add-vertex-step").click();
  await page.getByTestId("add-vertex-step").click();
  await expect(page.getByTestId("sequence-error")).toBeVisible();
  await expect(page.getByTestId("sg-create")).toBeDisabled();

  // Fix to a legal V-E-V pattern and create; an empty result is a valid 201 (FR-17).
  await page.getByRole("button", { name: "Remove" }).last().click();
  await page.getByTestId("add-edge-step").click();
  await page.getByTestId("add-vertex-step").click();
  await page.getByTestId("sg-create").click();
  await expect(page.getByTestId("subgraph-message")).toContainText("Created", {
    timeout: 20_000,
  });

  await page.getByRole("button", { name: "Delete" }).first().click();
  await expect(page.getByTestId("subgraph-message")).toContainText("Deleted");
});

test("save games: save now registers a row; load and delete demand typed confirmation", async ({
  page,
}) => {
  await registerSecuredInstance(page, "savegametest");
  // Ensure there is something to save.
  await createVertex(page, "person");

  await page.goto("/save-games");
  await page.getByTestId("save-now").click();
  await expect(page.getByTestId("savegame-message")).toContainText("Saved", { timeout: 20_000 });

  const row = page.locator('[data-testid^="savegame-row-"]').first();
  await expect(row).toBeVisible({ timeout: 20_000 });

  // Load demands the typed instance name.
  await row.getByRole("button", { name: "Load…" }).click();
  const confirmLoad = page.getByTestId("confirm-action");
  await expect(confirmLoad).toBeDisabled();
  await page.getByTestId("confirm-typed").fill("savegametest");
  await confirmLoad.click();
  await expect(page.getByTestId("savegame-message")).toContainText("Restored", { timeout: 20_000 });

  // Delete demands the typed instance name; the files checkbox is available.
  await page.locator('[data-testid^="savegame-row-"]').first().getByRole("button", { name: "Delete…" }).click();
  await expect(page.getByTestId("delete-files-toggle")).toBeVisible();
  await page.getByTestId("confirm-typed").fill("savegametest");
  await page.getByTestId("confirm-action").click();
  await expect(page.getByTestId("savegame-message")).toContainText("deleted", { timeout: 20_000 });
});

test("scenario 8: erasing a namespace demands its typed NAME (feature graph-namespaces)", async ({ page }) => {
  await registerSecuredInstance(page, "erasable");
  // Administration (Erase namespace) lives on the Save games screen now.
  await page.goto("/save-games");
  await page.getByTestId("tabularasa").click();

  // The erase is namespace-scoped: the gate is the NAMESPACE name, not the instance's.
  const confirm = page.getByTestId("confirm-action");
  await expect(confirm).toBeDisabled();
  await page.getByTestId("confirm-typed").fill("erasable");
  await expect(confirm).toBeDisabled();
  await page.getByTestId("confirm-typed").fill("default");
  await confirm.click();

  await expect(page.getByTestId("admin-message")).toContainText("erased", {
    timeout: 20_000,
  });

  // The count reads 0 in the top bar. No intro to dismiss: this screen is /save-games, a flat
  // route, where the auto-show stays silent by design.
  await expect.poll(async () => activeVertexCount(page), { timeout: 30_000 }).toBe(0);
});

test("scenario 9: an unreachable instance shows the disconnected state, not a blank screen", async ({
  page,
}) => {
  await page.goto("/");
  await page.getByTestId("instance-add").click();
  await page.getByTestId("instance-name").fill("down");
  await page.getByTestId("instance-url").fill("http://localhost:59998");
  await page.getByTestId("instance-save").click();
  await page.getByRole("radio", { name: "activate down" }).check();

  await page.goto("/q/default/browser");
  await expect(page.getByTestId("health-chip")).toHaveText("unreachable", {
    timeout: 20_000,
  });
  // NOT BLANK is the claim in the title, so it has to be asserted about the screen area, not just
  // the chip. This used to land on the Dashboard, whose ErrorBox rendered role=alert + "Retry" on a
  // failed /status; the Browser fetches nothing until asked, so there is no error box here. What
  // the shell does guarantee on a merely-unreachable instance is that the screen stays MOUNTED (a
  // 15s health blip must not throw away in-progress work) while the rail locks behind the gate.
  await expect(page.getByTestId("new-vertex-label")).toBeVisible();
  await expect(page.getByTestId("connection-guard")).toHaveCount(0);
  await expect(page.getByTestId("nav-browser")).toHaveAttribute("aria-disabled", "true");
  // And the instance overview still names the failure in words rather than leaving a blank row.
  await page.goto("/");
  await expect(page.getByTestId("instance-row-down")).toContainText("unreachable");
});

test("scenario 10 (instance default): assist is usable with zero config; editor fully usable", async ({
  page,
}) => {
  await registerSecuredInstance(page);
  await page.goto("/path");
  await page.getByTestId("toggle-advanced").click();
  await page.getByTestId("slot-filter-vertexfilter").click();

  // nl-assist-ux FR-1 (feature instance-config): the default routes through the active
  // instance, so the assist needs no configuration — the intent box and draft button are
  // present immediately, and the status line names the instance path.
  await expect(page.getByTestId("nl-intent")).toBeVisible();
  await expect(page.getByTestId("nl-generate")).toBeVisible();
  await expect(page.getByTestId("nl-backend-status")).toContainText("this instance");
  const editor = page.locator(".monaco-editor").first();
  await editor.click();
  await page.keyboard.press("Control+a");
  await page.keyboard.type("return (v) => true;", { delay: 10 });
  await expect(page.getByTestId("validation-valid")).toBeVisible({ timeout: 15_000 });
});

test("scenario 12 (graph-namespaces): create, populate, isolate, save all, drop, restore one", async ({
  page,
}) => {
  await registerSecuredInstance(page, "nstest");

  // Create "flights" through the Connect panel; the live URL preview names the call.
  await page.getByTestId("namespace-create-name").fill("flights");
  await expect(page.getByTestId("namespace-url-preview")).toHaveText("PUT /ns/flights");
  await page.getByTestId("namespace-create").click();
  await expect(page.getByTestId("namespace-row-flights")).toBeVisible({ timeout: 20_000 });

  // Switch to it: the namespace lands in the app URL and in the endpoint hint.
  // Switching FROM the Connect screen stays on the Connect screen: only the top bar changes.
  await page.getByTestId("namespace-switch-flights").click();
  await expect(page).toHaveURL(/\/$/);
  await expect(page.getByTestId("active-endpoint")).toContainText("/ns/flights/*");

  // Populate flights with a uniquely-labelled vertex (createVertex navigates to the
  // browser, which redirects into the ACTIVE namespace's screen).
  const label = `plane-${Date.now().toString(36)}`;
  await page.goto("/browser");
  await expect(page).toHaveURL(/\/q\/flights\/browser/);
  // "flights" is brand new, so it is empty and gets its own first-run show; the dismissal is
  // remembered per namespace, not per browser.
  await dismissFirstRunIfPresent(page);
  await page.getByTestId("new-vertex-label").fill(label);
  await page.getByTestId("create-vertex").click();
  await expect(page.getByTestId("mutation-message")).toContainText(label);

  // Isolation: the vertex is invisible from "default" (same screen, other namespace). The
  // switcher is the rich dropdown: filter, rows with counts, active/alias tags.
  await page.getByTestId("namespace-switcher").click();
  await page.getByTestId("namespace-filter").fill("def");
  await expect(page.getByTestId("namespace-option-flights")).not.toBeVisible();
  await page.getByTestId("namespace-option-default").click();
  await expect(page).toHaveURL(/\/q\/default\/browser/);
  await page.locator("#max-elements").fill("5000");
  await page.getByRole("button", { name: "Load", exact: true }).click();
  await page.getByTestId("bulk-filter").fill(label);
  await expect(page.locator("tr", { hasText: label })).toHaveCount(0);

  // Save ALL namespaces into one spanning entry; its manifest lists flights.
  await page.goto("/save-games");
  await page.getByTestId("save-now").click();
  await expect(page.getByTestId("savegame-message")).toContainText("Saved", { timeout: 20_000 });
  const entry = page.locator('[data-testid^="savegame-row-"]').first();
  await expect(entry.locator('[data-testid^="savegame-namespaces-"]')).toContainText("flights");

  // Drop flights (typed NAMESPACE name), then restore ONLY it out of the entry.
  await page.goto("/");
  await page.getByTestId("namespace-drop-flights").click();
  await expect(page.getByTestId("confirm-action")).toBeDisabled();
  await page.getByTestId("confirm-typed").fill("flights");
  await page.getByTestId("confirm-action").click();
  await expect(page.getByTestId("namespace-row-flights")).not.toBeVisible({ timeout: 20_000 });

  await page.goto("/save-games");
  await page.locator('[data-testid^="savegame-row-"]').first().getByRole("button", { name: "Load…" }).click();
  await page.getByTestId("load-namespace-select").locator("select").selectOption("flights");
  await page.getByTestId("confirm-typed").fill("nstest");
  await page.getByTestId("confirm-action").click();
  await expect(page.getByTestId("savegame-message")).toContainText("flights", { timeout: 20_000 });

  // The dropped namespace is back, with its saved content.
  await page.getByTestId("namespace-switcher").click();
  await page.getByTestId("namespace-option-flights").click();
  await page.goto("/browser");
  await expect(page).toHaveURL(/\/q\/flights\/browser/);
  await page.locator("#max-elements").fill("5000");
  await page.getByRole("button", { name: "Load", exact: true }).click();
  await page.getByTestId("bulk-filter").fill(label);
  await expect(page.locator("tr", { hasText: label }).first()).toBeVisible({ timeout: 20_000 });
});

/**
 * Regression (feature graph-namespaces): the Benchmark screen was Fallen-8-level and always wrote
 * into `default`, whatever the switcher said. Scenario 2 above cannot catch that, because it works
 * in `default` and so cannot tell a scoped write from a defaulted one - which is exactly why the bug
 * survived. This one uses a NON-default namespace and asserts `default` stays empty.
 */
test("scenario 13 (graph-namespaces): benchmark generation writes the SELECTED namespace", async ({
  page,
  request,
}) => {
  const auth = { Authorization: `Bearer ${API_KEY}` };
  const ns = "benchns";
  await request.delete(`/ns/${ns}`, { headers: auth });
  expect((await request.put(`/ns/${ns}`, { headers: auth })).status()).toBe(201);

  try {
    await registerSecuredInstance(page, "benchtest");

    await page.goto(`/q/${ns}/benchmarks`);
    // A namespace created for this test, so it is empty and gets its own first-run show over
    // this screen. The setup dismissed it for `default` only - the memory is per namespace.
    // BEFORE the heading assertion, not after: the show is a MODAL Radix dialog, so while it is
    // open every sibling carries aria-hidden, and getByRole reads the accessibility tree - the
    // h1 is not merely covered, it is unfindable. Asserting first was a race against the show's
    // own mount, and it lost about half the time.
    await dismissFirstRunIfPresent(page);
    // The screen names the graph it acts on (instance / namespace).
    await expect(page.getByRole("heading", { level: 1 })).toContainText(ns);

    await page.getByTestId("generate-sample").click();
    await expect(page.getByTestId("generate-result")).toBeVisible({ timeout: 30_000 });
    // The SERVER's answer, not the request: it names the namespace it wrote into.
    await expect(page.getByTestId("stat-into-namespace")).toHaveText(ns);
    await expect(page.getByTestId("stat-vertices-created")).toHaveText("200");
    // The header count follows the addressed namespace too (it used to report default's).
    await expect(page.getByText(/current graph: 200 vertices/)).toBeVisible({ timeout: 20_000 });

    // The bug, stated as an assertion: nothing landed in `default`.
    const inventory = await (await request.get("/ns", { headers: auth })).json();
    const vertices = Object.fromEntries(
      (inventory.namespaces as Array<{ name: string; vertexCount: number }>).map((entry) => [
        entry.name,
        entry.vertexCount,
      ]),
    );
    expect(vertices[ns]).toBe(200);
    expect(vertices.default).toBe(0);

    // The benchmark measures the same graph: 200 vertices x 5 distinct targets.
    await page.getByTestId("run-benchmark").click();
    await expect(page.getByTestId("benchmark-result")).toBeVisible({ timeout: 120_000 });
    await expect(page.getByTestId("stat-edges-per-run")).toHaveText("1,000");

    // The pre-namespace flat URL still lands on the active namespace's screen.
    await page.goto("/benchmarks");
    await expect(page).toHaveURL(new RegExp(`/q/${ns}/benchmarks`));
  } finally {
    await request.delete(`/ns/${ns}`, { headers: auth });
  }
});

test("scenario 11: nav stays locked until the active instance is connected AND authorized", async ({
  page,
}) => {
  await page.goto("/");

  // The default same-origin instance carries no key while this server requires one:
  // reachable but NOT authorized - everything beyond Connect must be locked, and the
  // instance overview must say why.
  await expect(page.getByTestId("health-chip")).toHaveText("unauthorized", {
    timeout: 20_000,
  });
  await expect(page.getByTestId("nav-browser")).toHaveAttribute("aria-disabled", "true");
  await expect(page.getByTestId("nav-canvas")).toHaveAttribute("aria-disabled", "true");
  await expect(
    page.getByTestId("instance-row-local").getByText("unauthorized — check the API key"),
  ).toBeVisible();

  // A deep link cannot bypass the gate either.
  await page.goto("/q/default/browser");
  await expect(page.getByTestId("connection-guard")).toBeVisible({ timeout: 20_000 });

  // With a keyed instance active the nav unlocks and actually navigates.
  await registerSecuredInstance(page);
  await expect(page.getByTestId("health-chip")).toHaveText("online", { timeout: 20_000 });
  const browser = page.getByTestId("nav-browser");
  await expect(browser).not.toHaveAttribute("aria-disabled", "true");
  await browser.click();
  await dismissFirstRunIfPresent(page);
  await expect(page).toHaveURL(/\/q\/default\/browser$/);
  await expect(page.getByTestId("new-vertex-label")).toBeVisible();
});

test("per-section help opens the docs for the current screen", async ({ page }) => {
  await registerSecuredInstance(page);

  // Land on a scoped screen; the shell resolves the legacy path to /q/{ns}/path.
  await page.goto("/path");
  await expect(page).toHaveURL(/\/q\/[^/]+\/path$/);

  // The per-section help button sits in the top bar next to the docs pill and is keyed to the
  // active section (feature studio-section-help).
  const help = page.getByTestId("section-help");
  await expect(help).toBeVisible();
  await expect(help).toHaveAttribute("title", "How path finding works");

  await help.click();
  const popover = page.getByTestId("section-help-popover");
  await expect(popover).toBeVisible();
  const firstLink = page.getByTestId("section-help-link").first();
  await expect(firstLink).toHaveAttribute(
    "href",
    "https://docs.fallen-8.com/path-finding/",
  );
  await expect(firstLink).toHaveAttribute("target", "_blank");

  // Escape closes it without leaving the app.
  await page.keyboard.press("Escape");
  await expect(popover).toBeHidden();
});
