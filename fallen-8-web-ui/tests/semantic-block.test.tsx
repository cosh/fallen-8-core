import { describe, expect, it } from "vitest";
import { useState } from "react";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { SemanticBlockEditor } from "../src/components/SemanticBlockEditor";
import { DEFAULT_SEMANTIC_DRAFT, type SemanticDraft } from "../src/lib/semantic";

/**
 * The shared semantic-block editor (feature element-embeddings): its gating mirrors the
 * server rules in the UI — query-text needs the provider, minScore/costBySimilarity are
 * the declarative slots, costBySimilarity is inert under DotProduct, and the whole block
 * is inert when a stored template owns the filters.
 */

function Harness({
  allowCost = true,
  costDisabledReason,
  providerEnabled = true as boolean | null,
  disabled = false,
  initial = {},
}: {
  allowCost?: boolean;
  costDisabledReason?: string;
  providerEnabled?: boolean | null;
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

  it("query-text unknown-provider hint points at Compute", () => {
    render(<Harness providerEnabled={null} initial={{ enabled: true, source: "text" }} />);
    expect(screen.getByTestId("t-sem-text-unavailable")).toHaveTextContent(/Compute the Graph shape/i);
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
