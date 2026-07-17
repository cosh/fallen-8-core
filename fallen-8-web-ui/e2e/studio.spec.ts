import { expect, test, type Page } from "@playwright/test";

/**
 * F8 Studio end-to-end scenarios (spec §9) against a live apiApp serving the built SPA.
 * The server runs with an API key ("e2e-key") and dynamic code enabled (see
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
  await expect(page.getByTestId("active-endpoint")).toHaveText("same origin");
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

test("scenario 1: connect, dashboard, health, disconnected overview", async ({ page }) => {
  await registerSecuredInstance(page);

  // A dead endpoint is visible in the overview before switching to it (FR-1a).
  await page.getByTestId("instance-add").click();
  await page.getByTestId("instance-name").fill("dead");
  await page.getByTestId("instance-url").fill("http://localhost:59999");
  await page.getByTestId("instance-save").click();
  await expect(
    page.getByTestId("instance-row-dead").getByText("unreachable"),
  ).toBeVisible({ timeout: 20_000 });

  await page.goto("/dashboard");
  await expect(page.getByTestId("stat-vertices")).toBeVisible();
  await expect(page.getByTestId("health-chip")).toHaveText("online");
});

test("scenario 2: one click generates the sample graph; benchmark shows structured numbers", async ({
  page,
}) => {
  await registerSecuredInstance(page);
  await page.goto("/dashboard");
  await page.getByTestId("generate-sample").click();
  await page.getByRole("button", { name: "Refresh" }).click();
  await expect
    .poll(
      async () => Number((await page.getByTestId("stat-vertices").textContent())?.replace(/\D/g, "")),
      { timeout: 30_000 },
    )
    .toBeGreaterThan(0);

  // Structured benchmark output rendered as stat tiles.
  await page.getByTestId("run-benchmark").click();
  await expect(page.getByTestId("benchmark-result")).toBeVisible({ timeout: 120_000 });
  await expect(page.getByTestId("stat-avg-tps")).not.toHaveText("—");
  await expect(page.getByTestId("stat-edges-per-run")).not.toHaveText("0");
});

test("scenario 3+4: mutate, browse, scan, hydrate, canvas", async ({ page }) => {
  await registerSecuredInstance(page);

  const source = await createVertex(page, "person");
  const target = await createVertex(page, "person");

  // Set a typed property, then look the vertex up (FR-5) and walk adjacency (FR-6).
  await page.locator("#mp-element").fill(String(source));
  await page.locator("#mp-id").fill("age");
  await page.getByLabel(/^value type$/).selectOption("System.Int32");
  await page.getByTestId("mp-value").fill("42");
  await page.getByRole("button", { name: "Set property" }).click();
  await expect(page.getByTestId("mutation-message")).toContainText("age");

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
});

test("scenario 5: delegate editor validates, blocks, then passes and the path runs", async ({
  page,
}) => {
  await registerSecuredInstance(page);

  // Deterministic three-vertex chain via the UI.
  const a = await createVertex(page, "person");
  const b = await createVertex(page, "person");
  const c = await createVertex(page, "person");
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
  await expect(page.getByTestId("savegame-message")).toContainText("Loaded", { timeout: 20_000 });

  // Delete demands the typed instance name; the files checkbox is available.
  await page.locator('[data-testid^="savegame-row-"]').first().getByRole("button", { name: "Delete…" }).click();
  await expect(page.getByTestId("delete-files-toggle")).toBeVisible();
  await page.getByTestId("confirm-typed").fill("savegametest");
  await page.getByTestId("confirm-action").click();
  await expect(page.getByTestId("savegame-message")).toContainText("deleted", { timeout: 20_000 });
});

test("scenario 8: tabula rasa demands the typed instance name", async ({ page }) => {
  await registerSecuredInstance(page, "erasable");
  await page.goto("/dashboard");
  await page.getByTestId("tabularasa").click();

  const confirm = page.getByTestId("confirm-action");
  await expect(confirm).toBeDisabled();
  await page.getByTestId("confirm-typed").fill("wrong-name");
  await expect(confirm).toBeDisabled();
  await page.getByTestId("confirm-typed").fill("erasable");
  await confirm.click();

  await expect(page.getByTestId("admin-message")).toContainText("erased", {
    timeout: 20_000,
  });
  await expect(page.getByTestId("stat-vertices")).toHaveText("0");
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

  await page.goto("/dashboard");
  await expect(page.getByRole("alert")).toBeVisible({ timeout: 20_000 });
  await expect(page.getByRole("button", { name: "Retry" })).toBeVisible();
  await expect(page.getByTestId("health-chip")).toHaveText("unreachable", {
    timeout: 20_000,
  });
});

test("scenario 10 (builtin default): assist is usable with zero config; editor fully usable", async ({
  page,
}) => {
  await registerSecuredInstance(page);
  await page.goto("/path");
  await page.getByTestId("toggle-advanced").click();
  await page.getByTestId("slot-filter-vertexfilter").click();

  // nl-assist-ux FR-1: the builtin backend needs no configuration — the intent box and
  // draft button are present immediately (whether the backend is running is reported by
  // the status line, which is environment-dependent and not asserted here).
  await expect(page.getByTestId("nl-intent")).toBeVisible();
  await expect(page.getByTestId("nl-generate")).toBeVisible();
  await expect(page.getByTestId("nl-backend-status")).toContainText("built-in");
  const editor = page.locator(".monaco-editor").first();
  await editor.click();
  await page.keyboard.press("Control+a");
  await page.keyboard.type("return (v) => true;", { delay: 10 });
  await expect(page.getByTestId("validation-valid")).toBeVisible({ timeout: 15_000 });
});
