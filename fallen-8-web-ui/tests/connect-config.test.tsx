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
import type { ConfigREST } from "../src/api/types";

/**
 * Connect · Configuration section (feature instance-config): the read-only semantic-provider
 * + observability view sourced from GET /config, and the observability details overlay. The
 * embedding-provider display moved here from the Dashboard, now unified with the chat gateway.
 */

const getConfigMock = vi.fn<(i: InstanceConfig, s?: AbortSignal) => Promise<ConfigREST | null>>();
vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return { ...original, getConfig: (i: InstanceConfig, s?: AbortSignal) => getConfigMock(i, s) };
});

import { ConfigurationPanel } from "../src/components/ConfigurationPanel";
import { SAME_ORIGIN_INSTANCE, useRegistry } from "../src/instances/registry";

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

function renderPanel() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <ConfigurationPanel />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  getConfigMock.mockReset();
  useRegistry.setState({ instances: [SAME_ORIGIN_INSTANCE], activeId: SAME_ORIGIN_INSTANCE.id });
});

describe("Connect Configuration panel", () => {
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

  it("opens the observability overlay with the env keys", async () => {
    getConfigMock.mockResolvedValue(config());
    renderPanel();
    await screen.findByTestId("config-embedding");

    await userEvent.click(screen.getByTestId("config-observability-configure"));
    const overlay = await screen.findByTestId("config-observability-overlay");
    expect(overlay).toHaveTextContent("Fallen8__Observability__Otlp__Endpoint");
    expect(overlay).toHaveTextContent("http://otel-collector:4317");
    // Grouped into three labelled sections so push (the live path) is not confused with the
    // off-by-default Prometheus scrape endpoint (feature studio-obs-config).
    expect(overlay).toHaveTextContent("Push (OTLP)");
    expect(overlay).toHaveTextContent("Pull (Prometheus scrape)");
    expect(overlay).toHaveTextContent("Statistics snapshot");
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

  it("degrades to an unavailable note when the instance rejects the read", async () => {
    getConfigMock.mockRejectedValue(new Error("unreachable"));
    renderPanel();

    await waitFor(() => expect(screen.getByTestId("config-unavailable")).toBeInTheDocument());
  });
});
