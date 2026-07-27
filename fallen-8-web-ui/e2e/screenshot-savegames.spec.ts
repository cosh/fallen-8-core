import { expect, test } from "@playwright/test";
import { readFileSync } from "node:fs";
import path from "node:path";

/**
 * Docs screenshot capture (list-caps / save-games reorder): the Save games screen with the
 * **Administration** section on top and the checkpoint registry below it, capped + scrolling.
 * Capture-only. Populates a small graph across three namespaces via REST, then writes enough
 * spanning save-game entries to fill the scrolling registry, so the image shows the real layout.
 *
 *   F8_SCREENSHOT=1 F8_UI_URL=http://127.0.0.1:5000 npx playwright test e2e/screenshot-savegames.spec.ts
 *
 * Output: docs/images/screen-savegames.png. Needs a DURABLE apiApp (not volatile — volatile
 * catalogs nothing, so the registry would stay empty); point its storage at a throwaway dir.
 */

const API_KEY = process.env.F8_E2E_API_KEY ?? "e2e-key";
const AUTH = { Authorization: `Bearer ${API_KEY}` };
const NDJSON = { ...AUTH, "Content-Type": "application/x-ndjson" };

test.skip(process.env.F8_SCREENSHOT !== "1", "docs screenshot capture (set F8_SCREENSHOT=1)");

test("capture the save-games screen (Administration on top, capped registry)", async ({
  page,
  request,
}) => {
  await page.setViewportSize({ width: 1440, height: 1000 });

  // A small, real dataset so the registry rows carry non-zero counts and multi-namespace lists.
  const jsonl = readFileSync(
    path.resolve(process.cwd(), "../samples/cyber-warfare.jsonl"),
    "utf8",
  );

  // Idempotent reset so the capture is re-runnable: drop every namespace's data and clear the
  // registry before populating (a fresh boot is already clean; this covers a mid-run retry).
  await request.head("/tabularasa/all", { headers: AUTH });
  const existing = (await (await request.get("/savegames", { headers: AUTH })).json()) as {
    id: string;
  }[];
  for (const g of existing) {
    await request.delete(`/savegames/${encodeURIComponent(g.id)}?deleteFiles=true`, {
      headers: AUTH,
    });
  }

  // default namespace: import the cyber sample (import requires an empty target — a fresh
  // durable boot with an empty storage dir gives exactly that).
  expect((await request.post("/bulk/import", { headers: NDJSON, data: jsonl })).ok()).toBeTruthy();

  // Two more namespaces so entries span several: one populated, one left empty.
  for (const ns of ["fraud-q3", "staging"]) {
    expect((await request.put(`/ns/${ns}`, { headers: AUTH })).ok()).toBeTruthy();
  }
  expect(
    (await request.post("/ns/fraud-q3/bulk/import", { headers: NDJSON, data: jsonl })).ok(),
  ).toBeTruthy();

  // Enough spanning checkpoints to fill the fixed-height registry and show it scrolling.
  for (let i = 0; i < 18; i++) {
    expect((await request.put("/save/all", { headers: AUTH })).ok()).toBeTruthy();
    await page.waitForTimeout(250); // spread the "saved at" times across seconds
  }

  // The app seeds a same-origin "local" instance without a key; give it the e2e key so it
  // authorizes (rather than adding a second "local").
  await page.goto("/");
  await page.getByRole("button", { name: "Edit" }).first().click();
  await page.getByLabel(/api key/i).fill(API_KEY);
  await page.getByTestId("instance-save").click();
  await page.getByRole("radio", { name: "activate local" }).check();
  await expect(page.getByTestId("active-endpoint")).toContainText("same origin");
  // The status query was cached "unauthorized" before the key was set and only refetches on its
  // interval; reload to fetch /status fresh (with the key) so the nav gate opens immediately.
  await page.reload();
  await expect(page.getByTestId("health-chip")).toContainText(/online/i, { timeout: 20_000 });

  await page.goto("/save-games");
  // The registry has rendered once its rows are present.
  await expect(page.getByTestId("administration")).toBeVisible();
  await expect(page.locator("[data-testid^='savegame-row-']").first()).toBeVisible({
    timeout: 30_000,
  });

  await page.screenshot({ path: "../docs/images/screen-savegames.png" });
});
