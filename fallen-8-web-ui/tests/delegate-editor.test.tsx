import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { DelegateValidationResult } from "../src/api/types";
import type { InstanceConfig } from "../src/instances/types";

/**
 * Delegate editor component behaviour (FR-23/25/26): commit gating on validation,
 * disabled NL assist without a backend, and the bounded invalid-then-valid refine loop.
 * Monaco is mocked to a textarea (marker rendering is covered by markers.test.ts + e2e);
 * all model calls are mocked (nl-assist spec §13).
 */

vi.mock("../src/delegate/monacoSetup", () => ({
  setupMonaco: () => {},
  monaco: {},
}));

vi.mock("@monaco-editor/react", () => ({
  default: ({
    value,
    onChange,
  }: {
    value: string;
    onChange?: (v: string | undefined) => void;
  }) => (
    <textarea
      data-testid="mock-editor"
      value={value}
      onChange={(e) => onChange?.(e.target.value)}
    />
  ),
}));

const validateMock = vi.fn<(...args: unknown[]) => Promise<DelegateValidationResult>>();
vi.mock("../src/api/endpoints", () => ({
  validateDelegate: (...args: unknown[]) => validateMock(...args),
}));

const chatMock = vi.fn<(...args: unknown[]) => Promise<string>>();
vi.mock("../src/delegate/nl/generate", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/delegate/nl/generate")>();
  return {
    ...original,
    chatWithModel: (...args: unknown[]) => chatMock(...args),
  };
});

import { DelegateEditor } from "../src/delegate/DelegateEditor";
import { useNlAssist, DEFAULT_NL_CONFIG } from "../src/delegate/nl/config";

const instance: InstanceConfig = {
  id: "t",
  name: "test",
  baseUrl: "http://f8.test",
  auth: { kind: "none" },
};

const INVALID: DelegateValidationResult = {
  valid: false,
  diagnostics: [
    {
      line: 1,
      column: 17,
      endLine: 1,
      endColumn: 21,
      id: "CS1061",
      message: "no such member",
      severity: "error",
    },
  ],
};
const VALID: DelegateValidationResult = { valid: true, diagnostics: [] };

function renderEditor(onCommit = vi.fn()) {
  render(
    <DelegateEditor
      instance={instance}
      delegateKind="VertexFilter"
      contextLabel="test slot"
      initialFragment=""
      onCommit={onCommit}
      onCancel={() => {}}
    />,
  );
  return onCommit;
}

beforeEach(() => {
  validateMock.mockReset();
  chatMock.mockReset();
  useNlAssist.setState({ config: DEFAULT_NL_CONFIG, leaveNoticeAccepted: false });
});

describe("delegate editor gating (FR-25)", () => {
  it("blocks commit while the fragment is invalid, enables it once valid", async () => {
    const user = userEvent.setup();
    validateMock.mockResolvedValue(INVALID);
    renderEditor();

    const editor = screen.getByTestId("mock-editor");
    await user.clear(editor);
    await user.type(editor, "return (v) => v.Nope;");

    await waitFor(() => expect(screen.getByTestId("validation-invalid")).toBeInTheDocument(), {
      timeout: 3000,
    });
    expect(screen.getByTestId("commit-fragment")).toBeDisabled();

    validateMock.mockResolvedValue(VALID);
    await user.clear(editor);
    await user.type(editor, "return (v) => true;");

    await waitFor(() => expect(screen.getByTestId("validation-valid")).toBeInTheDocument(), {
      timeout: 3000,
    });
    expect(screen.getByTestId("commit-fragment")).toBeEnabled();
  });

  it("re-blocks commit when the fragment is edited after passing validation (FR-25)", async () => {
    const user = userEvent.setup();
    validateMock.mockResolvedValue(VALID);
    renderEditor();

    const editor = screen.getByTestId("mock-editor");
    await user.clear(editor);
    await user.type(editor, "return (v) => true;");
    await waitFor(() => expect(screen.getByTestId("validation-valid")).toBeInTheDocument(), {
      timeout: 3000,
    });
    expect(screen.getByTestId("commit-fragment")).toBeEnabled();

    // Append text: the prior VALID result no longer describes the current fragment, so
    // commit must be blocked again immediately (before any re-validation resolves).
    await user.type(editor, " // stale");
    expect(screen.getByTestId("commit-fragment")).toBeDisabled();
  });

  it("treats the untouched opening snippet as empty = match everything", () => {
    renderEditor();
    expect(screen.getByText(/empty = match everything/i)).toBeInTheDocument();
    expect(screen.getByTestId("commit-fragment")).toBeEnabled();
  });

  it("commits the empty string for an empty fragment", async () => {
    const user = userEvent.setup();
    const onCommit = renderEditor();
    await user.click(screen.getByTestId("commit-fragment"));
    expect(onCommit).toHaveBeenCalledWith("");
  });
});

describe("NL assist (FR-26 / nl-assist spec)", () => {
  it("shows the disabled hint when no backend is configured", () => {
    renderEditor();
    expect(screen.getByTestId("nl-disabled-hint")).toBeInTheDocument();
    expect(screen.queryByTestId("nl-generate")).not.toBeInTheDocument();
  });

  it("runs the invalid-then-valid refine loop, keeping both attempts visible", async () => {
    const user = userEvent.setup();
    useNlAssist.setState({
      config: {
        ...DEFAULT_NL_CONFIG,
        endpoint: "http://localhost:11434",
        model: "phi4-mini",
        maxRetries: 2,
      },
      leaveNoticeAccepted: false,
    });
    chatMock
      .mockResolvedValueOnce("return (v) => v.Nope;")
      .mockResolvedValueOnce('return (v) => v.Label == "person";');
    validateMock.mockImplementation((...args: unknown[]) => {
      const fragment = args[2] as string;
      return Promise.resolve(fragment.includes("Nope") ? INVALID : VALID);
    });

    renderEditor();
    await user.type(screen.getByTestId("nl-intent"), "only persons");
    await user.click(screen.getByTestId("nl-generate"));

    await waitFor(
      () => {
        const attempts = screen.getByTestId("nl-attempts");
        expect(attempts.querySelectorAll("li")).toHaveLength(2);
      },
      { timeout: 5000 },
    );

    // Two model turns: initial generation + one refine carrying the diagnostics.
    expect(chatMock).toHaveBeenCalledTimes(2);
    const refineMessages = chatMock.mock.calls[1][1] as { content: string }[];
    expect(refineMessages.some((m) => m.content.includes("CS1061"))).toBe(true);

    // The final (valid) draft is in the editor, editable - never auto-submitted.
    expect(screen.getByTestId("mock-editor")).toHaveValue(
      'return (v) => v.Label == "person";',
    );
  });

  it("shows the leave-notice for non-loopback endpoints before the first send (FR-26.10)", () => {
    useNlAssist.setState({
      config: { ...DEFAULT_NL_CONFIG, endpoint: "https://api.example.com", model: "m" },
      leaveNoticeAccepted: false,
    });
    renderEditor();
    expect(screen.getByTestId("nl-leave-notice")).toBeInTheDocument();
    expect(screen.getByTestId("nl-generate")).toBeDisabled();
  });

  it("shows no leave-notice for loopback endpoints", () => {
    useNlAssist.setState({
      config: { ...DEFAULT_NL_CONFIG, endpoint: "http://localhost:11434", model: "m" },
      leaveNoticeAccepted: false,
    });
    renderEditor();
    expect(screen.queryByTestId("nl-leave-notice")).not.toBeInTheDocument();
  });
});
