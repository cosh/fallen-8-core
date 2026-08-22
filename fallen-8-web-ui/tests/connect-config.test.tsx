// MIT License
//
// connect-config.test.tsx
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

import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { InstanceConfig } from "../src/instances/types";
import type { ConfigREST, SettingREST } from "../src/api/types";

/**
 * Connect · Configuration card (features instance-config and configuration-surface): the read-only
 * semantic-provider + observability summary sourced from GET /config, and the Configure button that
 * opens the configuration surface.
 *
 * The provider cards and the observability one-liner stay ON the card, and their cases here are
 * deliberately unchanged from before the surface existed: keeping them passing is the point of the
 * split, because the reason someone opens Connect is to see which instance they are pointed at.
 */

const getConfigMock = vi.fn<(i: InstanceConfig, s?: AbortSignal) => Promise<ConfigREST | null>>();
vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return { ...original, getConfig: (i: InstanceConfig, s?: AbortSignal) => getConfigMock(i, s) };
});

import { ConfigurationPanel } from "../src/components/ConfigurationPanel";
import { SAME_ORIGIN_INSTANCE, useRegistry } from "../src/instances/registry";
import { PortalContainerContext } from "../src/app/studioConfig";
import { settingTestId } from "../src/lib/configCatalog";
import { openConfig, selectSection } from "./configSurface";

function setting(key: string, overrides: Partial<SettingREST> = {}): SettingREST {
  return {
    key,
    kind: "int",
    tier: "restart",
    applyMode: "restart",
    value: "1",
    source: "default",
    restartPending: false,
    ...overrides,
  };
}

function config(overrides: Partial<ConfigREST> = {}): ConfigREST {
  return {
    semantic: {
      embedding: {
        enabled: true,
        backend: "Ollama",
        modelName: "bge-m3",
        modelVersion: "",
        dimension: 1024,
        intendedMetric: "Cosine",
        loaded: true,
        resident: true,
        gpu: false,
      },
      chat: { enabled: true, backend: "Ollama", model: "phi4-f8-mini", loaded: false, resident: true, gpu: true },
    },
    observability: {
      otlpEnabled: true,
      otlpEndpoint: "http://otel-collector:4317",
      prometheusEnabled: false,
      prometheusRequireApiKey: false,
      tracingSamplingRatio: 1,
      statisticsElementBudget: 1_000_000,
      statisticsTopN: 20,
    },
    apiKeyRequired: false,
    ...overrides,
  };
}

function renderPanel(portalContainer?: HTMLElement) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <PortalContainerContext.Provider value={portalContainer}>
        <ConfigurationPanel />
      </PortalContainerContext.Provider>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  getConfigMock.mockReset();
  useRegistry.setState({ instances: [SAME_ORIGIN_INSTANCE], activeId: SAME_ORIGIN_INSTANCE.id });
});

describe("Connect Configuration card", () => {
  it("shows the semantic providers with GPU and the observability push target", async () => {
    getConfigMock.mockResolvedValue(config());
    renderPanel();

    const embedding = await screen.findByTestId("config-embedding");
    expect(embedding).toHaveTextContent("Ollama");
    expect(embedding).toHaveTextContent("bge-m3");
    expect(embedding).toHaveTextContent("1024d");
    expect(embedding).toHaveTextContent("Cosine");

    const chat = screen.getByTestId("config-chat");
    expect(chat).toHaveTextContent("phi4-f8-mini");
    expect(chat).toHaveTextContent("loaded · GPU"); // resident + gpu:true → loaded on GPU

    expect(screen.getByTestId("config-observability-summary")).toHaveTextContent(
      "pushing metrics + traces + logs to http://otel-collector:4317",
    );
  });

  it("keeps the card free of settings rows, which is the whole point of the split", async () => {
    getConfigMock.mockResolvedValue(config({ settings: [setting("Fallen8:Plugins:MaxCount")] }));
    renderPanel();

    await screen.findByTestId("config-embedding");
    // The row exists in the inventory and is still NOT on Connect until someone asks for it.
    expect(screen.queryByTestId(settingTestId("Fallen8:Plugins:MaxCount"))).toBeNull();
    expect(screen.getByTestId("config-settings-summary")).toHaveTextContent("1 setting, 0 set here");
  });

  it("opens the observability section with the env keys", async () => {
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(
      config({ settings: [setting("Fallen8:Observability:TracingSamplingRatio", { kind: "double" })] }),
    );
    renderPanel();
    await screen.findByTestId("config-embedding");

    await openConfig(user);
    await selectSection(user, "observability");

    const pane = await screen.findByTestId("config-observability-overlay");
    expect(pane).toHaveTextContent("Fallen8__Observability__Otlp__Endpoint");
    expect(pane).toHaveTextContent("http://otel-collector:4317");
    // Grouped into three labelled sections so push (the live path) is not confused with the
    // off-by-default Prometheus scrape endpoint (feature studio-obs-config).
    expect(pane).toHaveTextContent("Push (OTLP)");
    expect(pane).toHaveTextContent("Pull (Prometheus scrape)");
    expect(pane).toHaveTextContent("Statistics snapshot");
    // The sampling ratio is a writable key, so the fold-in renders it as an editable row rather than
    // as the read-only line the standalone overlay used to show.
    expect(screen.getByTestId(settingTestId("Fallen8:Observability:TracingSamplingRatio"))).toBeInTheDocument();
  });

  it("falls back to the read-only observability line when the instance publishes no inventory", async () => {
    // An older server answers GET /config without `settings`. Showing the effective value read-only
    // beats losing it, so the group keeps its row.
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(config({ settings: undefined }));
    renderPanel();
    await screen.findByTestId("config-embedding");

    await openConfig(user);
    const pane = await screen.findByTestId("config-observability-overlay");
    expect(pane).toHaveTextContent("Push (OTLP)");
    expect(screen.getByTestId("config-trace-sampling")).toHaveTextContent("1");
    // Grouped in the machine's own locale, which is what the moved code has always done: asserting a
    // comma here would pass in CI and fail on a German developer's machine.
    expect(screen.getByTestId("config-element-budget")).toHaveTextContent(
      (1_000_000).toLocaleString(),
    );
    expect(screen.getByTestId("config-settings-summary")).toHaveTextContent(
      "publishes no settings inventory",
    );
  });

  it("hides GPU when unknown, shows off states, and reports no exporter", async () => {
    getConfigMock.mockResolvedValue(
      config({
        semantic: {
          embedding: {
            enabled: false,
            backend: null,
            modelName: null,
            modelVersion: null,
            dimension: 0,
            intendedMetric: null,
            loaded: false,
          },
          chat: { enabled: true, backend: "Ollama", model: "phi4-f8-mini", loaded: true, resident: null, gpu: null },
        },
        observability: {
          otlpEnabled: false,
          otlpEndpoint: null,
          prometheusEnabled: false,
          prometheusRequireApiKey: false,
          tracingSamplingRatio: 1,
          statisticsElementBudget: 1_000_000,
          statisticsTopN: 20,
        },
      }),
    );
    renderPanel();

    const embedding = await screen.findByTestId("config-embedding");
    expect(embedding).toHaveTextContent("Off");
    const chat = screen.getByTestId("config-chat");
    expect(chat).not.toHaveTextContent("GPU");
    expect(chat).not.toHaveTextContent("CPU"); // residency unknown → no device shown
    expect(screen.getByTestId("config-observability-summary")).toHaveTextContent(
      "Off — no exporter configured",
    );
  });

  it("re-checks on demand via the Refresh button", async () => {
    getConfigMock.mockResolvedValue(config());
    renderPanel();
    await screen.findByTestId("config-embedding");
    const callsAfterLoad = getConfigMock.mock.calls.length;

    await userEvent.click(screen.getByTestId("config-refresh"));
    await waitFor(() => expect(getConfigMock.mock.calls.length).toBeGreaterThan(callsAfterLoad));
  });

  it("degrades to an unavailable note when the instance rejects the read, and offers no Configure", async () => {
    getConfigMock.mockRejectedValue(new Error("unreachable"));
    renderPanel();

    await waitFor(() => expect(screen.getByTestId("config-unavailable")).toBeInTheDocument());
    // No surface to open: a dialog listing nothing would be worse than the note explaining why.
    expect(screen.queryByTestId("config-configure")).toBeNull();
  });

  it("renders the surface into the host's portal container, not document.body", async () => {
    // The one gate on this. Every scoped style primitive is :where(.f8-studio), so a dialog that
    // escapes to document.body loses all of them in an embed, and in the library artifact it loses
    // the Tailwind utilities too. Nothing else in the suite can see that.
    const user = userEvent.setup();
    const host = document.createElement("div");
    document.body.appendChild(host);
    getConfigMock.mockResolvedValue(config({ settings: [setting("Fallen8:Plugins:MaxCount")] }));
    renderPanel(host);
    await screen.findByTestId("config-embedding");

    await openConfig(user);
    expect(host.querySelector('[data-testid="config-surface"]')).not.toBeNull();
  });

  it("leaves the page interactive after the surface is dismissed with Escape", async () => {
    // Radix sets pointer-events: none on the body while a modal is open. If the dialog does not clean
    // that up, the card behind it is dead and the failure looks like an unrelated broken button.
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(config({ settings: [setting("Fallen8:Plugins:MaxCount")] }));
    renderPanel();
    await screen.findByTestId("config-embedding");

    await openConfig(user);
    await user.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByTestId("config-surface")).toBeNull());

    const before = getConfigMock.mock.calls.length;
    await user.click(screen.getByTestId("config-refresh"));
    await waitFor(() => expect(getConfigMock.mock.calls.length).toBeGreaterThan(before));
  });

  it("searches across every section, not just the one on screen", async () => {
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(
      config({
        settings: [
          setting("Fallen8:Analytics:MaxConcurrentRuns"),
          setting("Fallen8:ChangeFeed:MaxSubscribers"),
          setting("Fallen8:Plugins:MaxCount"),
        ],
      }),
    );
    renderPanel();
    await screen.findByTestId("config-embedding");

    await openConfig(user);
    // Land on one section, then search for a substring that spans three others.
    await selectSection(user, "analytics");
    await user.type(screen.getByTestId("config-search"), "max");

    await waitFor(() =>
      expect(screen.getByTestId(settingTestId("Fallen8:ChangeFeed:MaxSubscribers"))).toBeInTheDocument(),
    );
    expect(screen.getByTestId(settingTestId("Fallen8:Analytics:MaxConcurrentRuns"))).toBeInTheDocument();
    expect(screen.getByTestId(settingTestId("Fallen8:Plugins:MaxCount"))).toBeInTheDocument();
  });

  it("says what a search covers when nothing matches, rather than showing an empty pane", async () => {
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(config({ settings: [setting("Fallen8:Plugins:MaxCount")] }));
    renderPanel();
    await screen.findByTestId("config-embedding");

    await openConfig(user);
    await user.type(screen.getByTestId("config-search"), "zzzz");

    const note = await screen.findByTestId("config-no-matches");
    // The honest part: the instance publishes no description for a key, so search cannot match one.
    expect(note).toHaveTextContent(/cannot match what a key does/);
  });

  it("narrows to the rows a filter names, and counts what it is hiding", async () => {
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(
      config({
        settings: [
          setting("Fallen8:Analytics:DefaultTimeBudgetSeconds"),
          setting("Fallen8:Analytics:MaxTimeBudgetSeconds", { source: "override" }),
          setting("Fallen8:Analytics:MaxConcurrentRuns"),
        ],
      }),
    );
    renderPanel();
    await screen.findByTestId("config-embedding");

    await openConfig(user);
    await selectSection(user, "analytics");
    expect(screen.getByTestId("config-filter-count-analytics")).toHaveTextContent("3 settings");

    await user.click(screen.getByTestId("config-filter-setHere"));
    await waitFor(() =>
      expect(screen.getByTestId("config-filter-count-analytics")).toHaveTextContent("1 of 3 settings"),
    );
    expect(screen.getByTestId(settingTestId("Fallen8:Analytics:MaxTimeBudgetSeconds"))).toBeInTheDocument();
    expect(screen.queryByTestId(settingTestId("Fallen8:Analytics:MaxConcurrentRuns"))).toBeNull();
  });

  it("shows a section this version of Studio does not group, rather than dropping its keys", async () => {
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(config({ settings: [setting("Fallen8:Quantum:Entanglement")] }));
    renderPanel();
    await screen.findByTestId("config-embedding");

    await openConfig(user);
    await selectSection(user, "other");
    expect(screen.getByTestId(settingTestId("Fallen8:Quantum:Entanglement"))).toBeInTheDocument();
  });
});
