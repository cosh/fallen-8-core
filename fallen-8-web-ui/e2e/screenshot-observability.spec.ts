import { expect, test } from "@playwright/test";

/**
 * Docs screenshot capture (feature studio-obs-config): the observability config overlay, grouped
 * into Push (OTLP) / Pull (Prometheus scrape) / Statistics sections. Run against a live apiApp
 * that has an OTLP endpoint configured, so the Push section shows a real endpoint:
 *
 *   F8_UI_URL=http://127.0.0.1:5000 npx playwright test e2e/screenshot-observability.spec.ts
 *
 * Output: docs/images/screen-connect-observability.png.
 */

const API_KEY = process.env.F8_E2E_API_KEY ?? "e2e-key";

// Capture-only: skipped in the normal e2e run, whose webServer has no OTLP endpoint (the Push
// section would render "off" and overwrite the good docs image). Run it deliberately against an
// OTLP-configured app: F8_SCREENSHOT=1 F8_UI_URL=http://127.0.0.1:5000 npx playwright test.
test.skip(process.env.F8_SCREENSHOT !== "1", "docs screenshot capture (set F8_SCREENSHOT=1)");

test("capture the observability config overlay", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });

  // Register a secured same-origin instance through the real Connect screen.
  await page.goto("/");
  await page.getByTestId("instance-add").click();
  await page.getByTestId("instance-name").fill("e2e");
  await page.getByTestId("instance-url").fill("");
  await page.getByLabel(/api key/i).fill(API_KEY);
  await page.getByTestId("instance-save").click();
  await page.getByRole("radio", { name: "activate e2e" }).check();
  await expect(page.getByTestId("active-endpoint")).toContainText("same origin");

  // Wait for the read-only Configuration panel, then open the observability overlay.
  await page.getByTestId("config-embedding").waitFor();
  await page.getByTestId("config-observability-configure").click();
  await page.getByTestId("config-observability-overlay").waitFor();
  await expect(page.getByText("Push (OTLP)")).toBeVisible();
  await expect(page.getByText("Pull (Prometheus scrape)")).toBeVisible();
  await expect(page.getByText("Statistics snapshot")).toBeVisible();

  // Capture the overlay over the dimmed Connect screen (matches how a user sees it).
  await page.screenshot({ path: "../docs/images/screen-connect-observability.png" });
});
