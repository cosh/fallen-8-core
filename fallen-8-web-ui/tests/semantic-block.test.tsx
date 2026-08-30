// MIT License
//
// semantic-block.test.tsx
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

import { describe, expect, it } from "vitest";
import { useState } from "react";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { SemanticBlockEditor } from "../src/components/SemanticBlockEditor";
import { DEFAULT_SEMANTIC_DRAFT, type SemanticDraft } from "../src/lib/semantic";
import type { EmbeddingProviderStatsREST } from "../src/api/types";

/**
 * The shared semantic-block editor (feature element-embeddings): its gating mirrors the
 * server rules in the UI — query-text needs the provider, minScore/costBySimilarity are
 * the declarative slots, costBySimilarity is inert under DotProduct, and the whole block
 * is inert when a stored template owns the filters.
 */

const PROVIDER: EmbeddingProviderStatsREST = {
  enabled: true,
  backend: "Nahil",
  modelName: "bge-m3",
  modelVersion: "",
  dimension: 1024,
  intendedMetric: "Cosine",
  loaded: true,
};

function Harness({
  allowCost = true,
  costDisabledReason,
  providerEnabled = true as boolean | null,
  provider,
  disabled = false,
  initial = {},
}: {
  allowCost?: boolean;
  costDisabledReason?: string;
  providerEnabled?: boolean | null;
  provider?: EmbeddingProviderStatsREST | null;
  disabled?: boolean;
  initial?: Partial<SemanticDraft>;
}) {
  const [draft, setDraft] = useState<SemanticDraft>({
    ...DEFAULT_SEMANTIC_DRAFT,
    ...initial,
  });
  return (
    <SemanticBlockEditor
      draft={draft}
      onChange={(patch) => setDraft((d) => ({ ...d, ...patch }))}
      allowCost={allowCost}
      costDisabledReason={costDisabledReason}
      providerEnabled={providerEnabled}
      provider={provider}
      embeddingNames={["default", "title"]}
      idPrefix="t"
      disabled={disabled}
    />
  );
}

describe("SemanticBlockEditor", () => {
  it("collapses to a reason when disabled (e.g. a stored template is selected)", () => {
    render(<Harness disabled />);
    expect(screen.getByTestId("t-semantic-disabled")).toBeInTheDocument();
    expect(screen.queryByTestId("t-semantic-enable")).not.toBeInTheDocument();
  });

  it("reveals the controls only once enabled", async () => {
    const user = userEvent.setup();
    render(<Harness />);
    expect(screen.queryByTestId("t-sem-source")).not.toBeInTheDocument();
    await user.click(screen.getByTestId("t-semantic-enable"));
    expect(screen.getByTestId("t-sem-source")).toBeInTheDocument();
    expect(screen.getByTestId("t-sem-vector")).toBeInTheDocument();
  });

  it("disables query text when the provider is off, with a reason", async () => {
    const user = userEvent.setup();
    render(<Harness providerEnabled={false} initial={{ enabled: true, source: "text" }} />);
    expect(screen.getByTestId("t-sem-text")).toBeDisabled();
    expect(screen.getByTestId("t-sem-text-unavailable")).toHaveTextContent(/provider is off/i);
    // The build error blocks submit upstream, surfaced inline here.
    expect(screen.getByTestId("t-sem-error")).toBeInTheDocument();
    await user.click(screen.getByTestId("t-semantic-enable")); // toggling off clears the error
    expect(screen.queryByTestId("t-sem-error")).not.toBeInTheDocument();
  });

  it("query-text unknown-provider hint suggests pasting a vector", () => {
    render(<Harness providerEnabled={null} initial={{ enabled: true, source: "text" }} />);
    expect(screen.getByTestId("t-sem-text-unavailable")).toHaveTextContent(/not reported by this server/i);
  });

  it("names the backend and embedding function that will embed the text", () => {
    render(
      <Harness provider={PROVIDER} initial={{ enabled: true, source: "text" }} />,
    );
    expect(screen.getByTestId("t-sem-text-provenance")).toHaveTextContent(
      "embeds on this instance via Nahil · bge-m3",
    );
  });

  it("says nothing about a provider that cannot embed, so the two never both show", () => {
    render(
      <Harness
        providerEnabled={false}
        provider={PROVIDER}
        initial={{ enabled: true, source: "text" }}
      />,
    );
    expect(screen.queryByTestId("t-sem-text-provenance")).not.toBeInTheDocument();
    expect(screen.getByTestId("t-sem-text-unavailable")).toBeInTheDocument();
  });

  it("stays silent when the caller holds no provider snapshot", () => {
    render(<Harness initial={{ enabled: true, source: "text" }} />);
    expect(screen.queryByTestId("t-sem-text-provenance")).not.toBeInTheDocument();
  });

  it("falls back to the dimension when the provider reports no model name", () => {
    render(
      <Harness
        provider={{ ...PROVIDER, modelName: null, backend: "Onnx" }}
        initial={{ enabled: true, source: "text" }}
      />,
    );
    expect(screen.getByTestId("t-sem-text-provenance")).toHaveTextContent(
      "embeds on this instance via Onnx · 1024d",
    );
  });

  it("shows the version when the embedding function carries one", () => {
    render(
      <Harness
        provider={{ ...PROVIDER, modelVersion: "v2" }}
        initial={{ enabled: true, source: "text" }}
      />,
    );
    expect(screen.getByTestId("t-sem-text-provenance")).toHaveTextContent("bge-m3@v2");
  });

  it("costBySimilarity is inert under DotProduct and absent when cost is not allowed", async () => {
    const user = userEvent.setup();
    const { rerender } = render(
      <Harness initial={{ enabled: true, metric: "DotProduct" }} />,
    );
    expect(screen.getByTestId("t-sem-cost")).toBeDisabled();

    rerender(<Harness allowCost={false} initial={{ enabled: true }} />);
    // A fresh Harness instance — re-enable and confirm the cost control is gone entirely.
    await user.click(screen.getByTestId("t-semantic-enable"));
    expect(screen.queryByTestId("t-sem-cost")).not.toBeInTheDocument();
  });

  it("shows the minScore field only when the filter is toggled on", async () => {
    const user = userEvent.setup();
    render(<Harness initial={{ enabled: true }} />);
    expect(screen.queryByTestId("t-sem-minscore")).not.toBeInTheDocument();
    await user.click(screen.getByTestId("t-sem-minscore-enable"));
    expect(screen.getByTestId("t-sem-minscore")).toBeInTheDocument();
  });

  it("disables costBySimilarity with a reason when costDisabledReason is set (e.g. BLS)", () => {
    render(
      <Harness initial={{ enabled: true }} costDisabledReason="BLS ignores costs — use DIJKSTRA" />,
    );
    expect(screen.getByTestId("t-sem-cost")).toBeDisabled();
    expect(screen.getByTestId("t-sem-cost-disabled")).toHaveTextContent(/BLS ignores costs/);
  });

  it("clears a stale costBySimilarity when switching metric to DotProduct", async () => {
    const user = userEvent.setup();
    render(<Harness initial={{ enabled: true, costBySimilarity: true }} />);
    expect(screen.getByTestId("t-sem-cost")).toBeChecked();
    await user.selectOptions(screen.getByTestId("t-sem-metric"), "DotProduct");
    // Now disabled AND unchecked — never stranded checked-but-disabled (blocking submit).
    expect(screen.getByTestId("t-sem-cost")).toBeDisabled();
    expect(screen.getByTestId("t-sem-cost")).not.toBeChecked();
  });
});
