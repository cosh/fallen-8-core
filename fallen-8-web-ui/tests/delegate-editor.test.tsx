// MIT License
//
// delegate-editor.test.tsx
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
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type {
  ChatProviderStatsREST,
  DelegateValidationResult,
  StatusREST,
} from "../src/api/types";
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
// Spread the original: the NL panel's ambient status line reads the /status cache row, and a
// replacement factory exporting only validateDelegate leaves getStatus undefined at import time.
vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return { ...original, validateDelegate: (...args: unknown[]) => validateMock(...args) };
});

import type { NlChatResult } from "../src/delegate/nl/generate";

const chatMock = vi.fn<(...args: unknown[]) => Promise<NlChatResult>>();
const probeMock = vi.fn<() => Promise<boolean>>();
vi.mock("../src/delegate/nl/generate", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/delegate/nl/generate")>();
  return {
    ...original,
    // The panel routes through generateChat (instance-gateway or custom); mock that seam.
    generateChat: (...args: unknown[]) => chatMock(...args),
    probeEndpoint: () => probeMock(),
  };
});

/** Draft with no stats — the transport's shape when a provider reports nothing. */
const draft = (content: string): NlChatResult => ({ content, stats: null });

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

const CHAT: ChatProviderStatsREST = {
  enabled: true,
  backend: "Nahil",
  model: "phi4-f8-mini",
  loaded: true,
};

function statusWithChat(chat: ChatProviderStatsREST): StatusREST {
  return {
    vertexCount: 0,
    edgeCount: 0,
    usedMemory: 0,
    availableIndexPlugins: [],
    availablePathPlugins: [],
    availableAnalyticsPlugins: [],
    availableServicePlugins: [],
    chat,
  };
}

/**
 * The panel reads /status through the shared cache row of the NAMESPACE-BOUND active instance.
 * The active instance here is the registry's own same-origin default ("local"), not the editor's
 * `instance` prop, and binding appends the namespace - hence "local/default". Seeding it keeps
 * the row warm so no jsdom fetch is attempted.
 */
function renderEditorHandle(onCommit = vi.fn(), chat: ChatProviderStatsREST = CHAT) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(["local/default", "status"], statusWithChat(chat));
  const utils = render(
    <QueryClientProvider client={client}>
      <DelegateEditor
        instance={instance}
        delegateKind="VertexFilter"
        contextLabel="test slot"
        initialFragment=""
        onCommit={onCommit}
        onCancel={() => {}}
      />
    </QueryClientProvider>,
  );
  return { ...utils, onCommit };
}

function renderEditor(onCommit = vi.fn(), chat?: ChatProviderStatsREST) {
  return renderEditorHandle(onCommit, chat).onCommit;
}

beforeEach(() => {
  validateMock.mockReset();
  chatMock.mockReset();
  probeMock.mockReset();
  // Pending by default so the probe's state update can't fire outside act(); the status
  // line then stays in its "checking…" state, which is all these tests assert on.
  probeMock.mockReturnValue(new Promise<boolean>(() => {}));
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

describe("NL assist (FR-26 / nl-assist + nl-assist-ux specs)", () => {
  it("is usable with zero configuration — instance default (nl-assist-ux FR-1, feature instance-config)", () => {
    renderEditor();
    expect(screen.getByTestId("nl-intent")).toBeInTheDocument();
    expect(screen.getByTestId("nl-generate")).toBeInTheDocument();
    // The ambient destination, read from /status. Never the connection registry's name, which
    // is not a backend and cannot be one.
    expect(screen.getByTestId("nl-backend-status")).toHaveTextContent(
      "this instance · /chat → Nahil · phi4-f8-mini",
    );
    expect(screen.queryByTestId("nl-disabled-hint")).not.toBeInTheDocument();
  });

  it("shows the disabled hint when custom mode has no endpoint (FR-26.8)", () => {
    useNlAssist.setState({
      config: { ...DEFAULT_NL_CONFIG, mode: "custom", endpoint: "" },
      leaveNoticeAccepted: false,
    });
    renderEditor();
    expect(screen.getByTestId("nl-disabled-hint")).toBeInTheDocument();
    expect(screen.queryByTestId("nl-generate")).not.toBeInTheDocument();
  });

  it("runs the invalid-then-valid refine loop, keeping both attempts visible", async () => {
    const user = userEvent.setup();
    useNlAssist.setState({
      config: { ...DEFAULT_NL_CONFIG, maxRetries: 2 },
      leaveNoticeAccepted: false,
    });
    chatMock
      .mockResolvedValueOnce(draft("return (v) => v.Nope;"))
      .mockResolvedValueOnce(draft('return (v) => v.Label == "person";'));
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
    // generateChat(config, instance, messages, signal) — messages is the 3rd arg.
    expect(chatMock).toHaveBeenCalledTimes(2);
    const refineMessages = chatMock.mock.calls[1][2] as { content: string }[];
    expect(refineMessages.some((m) => m.content.includes("CS1061"))).toBe(true);

    // The final (valid) draft is in the editor, editable - never auto-submitted.
    expect(screen.getByTestId("mock-editor")).toHaveValue(
      'return (v) => v.Label == "person";',
    );
  });

  it("accumulates drafts across runs and restores a clicked one (nl-assist-ux FR-6/7)", async () => {
    const user = userEvent.setup();
    validateMock.mockResolvedValue(VALID);
    chatMock
      .mockResolvedValueOnce(draft("return (v) => v.Id < 30;"))
      .mockResolvedValueOnce(draft('return (v) => v.Label == "person";'));

    renderEditor();
    await user.type(screen.getByTestId("nl-intent"), "small ids");
    await user.click(screen.getByTestId("nl-generate"));
    await waitFor(() =>
      expect(screen.getByTestId("nl-attempts").querySelectorAll("li")).toHaveLength(1),
    );

    // Second run does NOT reset the history — numbering continues.
    await user.click(screen.getByTestId("nl-generate"));
    await waitFor(() =>
      expect(screen.getByTestId("nl-attempts").querySelectorAll("li")).toHaveLength(2),
    );
    expect(screen.getByRole("button", { name: /draft 1/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /draft 2/ })).toBeInTheDocument();
    expect(screen.getByTestId("mock-editor")).toHaveValue(
      'return (v) => v.Label == "person";',
    );

    // Clicking a prior draft loads it back into the editor.
    await user.click(screen.getByRole("button", { name: /draft 1/ }));
    expect(screen.getByTestId("mock-editor")).toHaveValue("return (v) => v.Id < 30;");

    // The re-draft of the same intent asked for a distinct variant (FR-8).
    const secondRunMessages = chatMock.mock.calls[1][2] as { content: string }[];
    expect(
      secondRunMessages.some((m) => m.content.includes("return (v) => v.Id < 30;")),
    ).toBe(true);

    await user.click(screen.getByTestId("nl-clear-attempts"));
    expect(screen.queryByTestId("nl-attempts")).not.toBeInTheDocument();
  });

  it("captures a rated draft as a training example, locally and without any network call (FL-2)", async () => {
    const user = userEvent.setup();
    validateMock.mockResolvedValue(VALID);
    chatMock.mockResolvedValueOnce(draft('return (v) => v.Label == "person";'));

    renderEditor();
    await user.type(screen.getByTestId("nl-intent"), "only persons");
    await user.click(screen.getByTestId("nl-generate"));
    await waitFor(() =>
      expect(screen.getByTestId("nl-attempts").querySelectorAll("li")).toHaveLength(1),
    );

    // No export affordance until a draft is rated.
    expect(screen.queryByTestId("nl-export-training")).not.toBeInTheDocument();

    // jsdom implements neither URL.createObjectURL nor a navigating click - DEFINE stubs
    // (spyOn needs an existing prop) so the export path runs and is observable.
    const created: Blob[] = [];
    const original = {
      create: URL.createObjectURL,
      revoke: URL.revokeObjectURL,
      fetch: globalThis.fetch,
    };
    URL.createObjectURL = vi.fn((blob: Blob) => {
      created.push(blob);
      return "blob:mock";
    }) as typeof URL.createObjectURL;
    URL.revokeObjectURL = vi.fn() as typeof URL.revokeObjectURL;
    globalThis.fetch = vi.fn() as typeof globalThis.fetch;
    const clickSpy = vi.spyOn(HTMLElement.prototype, "click").mockImplementation(() => {});

    try {
      await user.click(screen.getByRole("button", { name: "👍" }));
      await user.click(screen.getByTestId("nl-export-training"));

      expect(clickSpy).toHaveBeenCalledTimes(1);
      expect(globalThis.fetch).not.toHaveBeenCalled(); // capture is local; nothing is POSTed
      expect(created).toHaveLength(1);

      // jsdom's Blob has no .text(); read the captured payload via FileReader.
      const payload = await new Promise<string>((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve(reader.result as string);
        reader.onerror = () => reject(reader.error);
        reader.readAsText(created[0]);
      });
      const rows = payload.trim().split("\n").map((line) => JSON.parse(line));
      expect(rows).toHaveLength(1);
      expect(rows[0]).toMatchObject({
        delegateKind: "VertexFilter",
        intent: "only persons",
        fragment: 'return (v) => v.Label == "person";',
        verdict: "up",
      });
      expect(typeof rows[0].ts).toBe("number");
    } finally {
      URL.createObjectURL = original.create;
      URL.revokeObjectURL = original.revoke;
      globalThis.fetch = original.fetch;
      clickSpy.mockRestore();
    }
  });

  it("pretty-prints a long one-line draft before inserting it (nl-assist-ux FR-9)", async () => {
    const user = userEvent.setup();
    validateMock.mockResolvedValue(VALID);
    chatMock.mockResolvedValueOnce(
      draft(
        'return (v) => v.Label == "person" && v.TryGetProperty(out int age, "age") && age > 30 && v.Id < 10;',
      ),
    );

    renderEditor();
    await user.type(screen.getByTestId("nl-intent"), "persons over 30 with small ids");
    await user.click(screen.getByTestId("nl-generate"));

    await waitFor(() =>
      expect(screen.getByTestId("mock-editor")).toHaveValue(
        [
          "return (v) =>",
          '    v.Label == "person"',
          '    && v.TryGetProperty(out int age, "age")',
          "    && age > 30",
          "    && v.Id < 10;",
        ].join("\n"),
      ),
    );
    // The formatted text is what got validated, so markers line up with the editor.
    expect(validateMock).toHaveBeenCalledWith(
      expect.anything(),
      expect.anything(),
      expect.stringContaining("return (v) =>\n"),
      expect.anything(),
    );
  });

  it("renders generation stats per attempt when the provider reports them (nl-assist-ux FR-5)", async () => {
    const user = userEvent.setup();
    validateMock.mockResolvedValue(VALID);
    chatMock.mockResolvedValueOnce({
      content: "return (v) => true;",
      stats: {
        promptTokens: 812,
        completionTokens: 24,
        durationMs: 3200,
        tokensPerSecond: 8,
        backend: "Nahil",
        raw: { backend: "Nahil", eval_count: 24, total_duration: 3_200_000_000 },
      },
    });

    renderEditor();
    await user.type(screen.getByTestId("nl-intent"), "anything");
    await user.click(screen.getByTestId("nl-generate"));

    await waitFor(() =>
      expect(screen.getByTestId("nl-attempts")).toHaveTextContent("812→24 tok"),
    );
    expect(screen.getByTestId("nl-attempts")).toHaveTextContent("3.2s");
    // The backend is the last segment of the line, not a row of its own.
    expect(screen.getByTestId("nl-attempts")).toHaveTextContent("8.0 tok/s · Nahil");
    expect(screen.getByText("raw stats")).toBeInTheDocument();
    expect(screen.getByTestId("nl-attempts")).toHaveTextContent("eval_count");

    // Regression: the stats used to render in the label's own flex row with `shrink-0`, which
    // truncated "draft 1 (in editor)" down to "d…" in the panel's 288px sidebar. They belong
    // below the label, where they cost the label no width.
    const label = screen.getByRole("button", { name: /^draft 1/ });
    expect(label.textContent).toBe("draft 1 (in editor)");
    expect(label.parentElement?.textContent).not.toContain("tok");
  });

  it("keeps a draft's own backend after the ambient backend changes (model-providers decision 5)", async () => {
    const user = userEvent.setup();
    validateMock.mockResolvedValue(VALID);
    // The response says Nahil served this call; the instance's CURRENT configuration says
    // Anthropic. Inferring the per-draft label from configuration is the bug this feature fixes,
    // so the two must disagree on screen.
    chatMock.mockResolvedValueOnce({
      content: "return (v) => true;",
      stats: {
        completionTokens: 12,
        durationMs: 1000,
        tokensPerSecond: 12,
        backend: "Nahil",
        raw: { backend: "Nahil" },
      },
    });

    renderEditor(vi.fn(), {
      enabled: true,
      backend: "Anthropic",
      model: "claude-opus-5",
      loaded: false,
    });
    await user.type(screen.getByTestId("nl-intent"), "anything");
    await user.click(screen.getByTestId("nl-generate"));

    await waitFor(() =>
      expect(screen.getByTestId("nl-attempts")).toHaveTextContent("12.0 tok/s · Nahil"),
    );
    expect(screen.getByTestId("nl-attempts")).not.toHaveTextContent("Anthropic");
    // The ambient line is the one that follows the configuration.
    expect(screen.getByTestId("nl-backend-status")).toHaveTextContent(
      "this instance · /chat → Anthropic · claude-opus-5",
    );
  });

  it("says chat is off rather than naming a backend it cannot know", () => {
    renderEditor(vi.fn(), { enabled: false, backend: null, model: null, loaded: false });
    expect(screen.getByTestId("nl-backend-status")).toHaveTextContent(
      "this instance · /chat → chat is off on this instance",
    );
  });

  it("says the backend is unreported when a server answers without one", () => {
    renderEditor(vi.fn(), { enabled: true, backend: null, model: null, loaded: false });
    expect(screen.getByTestId("nl-backend-status")).toHaveTextContent(
      "backend not reported by this server",
    );
  });

  it("shows the leave-notice for non-loopback endpoints before the first send (FR-26.10)", () => {
    useNlAssist.setState({
      config: {
        ...DEFAULT_NL_CONFIG,
        mode: "custom",
        endpoint: "https://api.example.com",
        model: "m",
      },
      leaveNoticeAccepted: false,
    });
    renderEditor();
    expect(screen.getByTestId("nl-leave-notice")).toBeInTheDocument();
    expect(screen.getByTestId("nl-generate")).toBeDisabled();
  });

  it("asks for no API key for the Ollama kind, only for openai-compatible (FR-26.12)", async () => {
    const user = userEvent.setup();
    useNlAssist.setState({
      config: { ...DEFAULT_NL_CONFIG, mode: "custom", endpoint: "http://localhost:11434" },
      leaveNoticeAccepted: false,
    });
    renderEditor();
    await user.click(screen.getByRole("button", { name: "configure" }));

    // The Ollama kind (the default local phi4-mini setup) shows no key field.
    expect(screen.queryByLabelText(/api key/i)).not.toBeInTheDocument();
    expect(screen.getByTestId("nl-no-key-hint")).toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText("api"), "openai");
    expect(screen.getByLabelText(/api key/i)).toBeInTheDocument();
    expect(screen.queryByTestId("nl-no-key-hint")).not.toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText("api"), "ollama");
    expect(screen.queryByLabelText(/api key/i)).not.toBeInTheDocument();
  });

  it("instance config shows no endpoint fields; a preset prefills custom (nl-assist-ux FR-3)", async () => {
    const user = userEvent.setup();
    renderEditor();
    await user.click(screen.getByRole("button", { name: "configure" }));

    // Instance mode: nothing to configure (routed through the active instance).
    expect(screen.getByTestId("nl-instance-hint")).toBeInTheDocument();
    expect(screen.queryByLabelText("endpoint")).not.toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText("backend"), "custom");
    await user.selectOptions(screen.getByLabelText("preset"), "OpenAI");
    expect(screen.getByLabelText("endpoint")).toHaveValue("https://api.openai.com/v1");
    expect(screen.getByLabelText("api")).toHaveValue("openai");
    expect(useNlAssist.getState().config.model).toBe("gpt-4o-mini");
  });

  it("shows no leave-notice for loopback endpoints", () => {
    useNlAssist.setState({
      config: {
        ...DEFAULT_NL_CONFIG,
        mode: "custom",
        endpoint: "http://localhost:11434",
        model: "m",
      },
      leaveNoticeAccepted: false,
    });
    renderEditor();
    expect(screen.queryByTestId("nl-leave-notice")).not.toBeInTheDocument();
  });

  it("shows the newest draft on top and flags unrated drafts until judged (nl-assist-draft-review-ux)", async () => {
    const user = userEvent.setup();
    validateMock.mockResolvedValue(VALID);
    chatMock
      .mockResolvedValueOnce(draft("return (v) => v.Id < 30;"))
      .mockResolvedValueOnce(draft('return (v) => v.Label == "person";'));

    renderEditor();
    await user.type(screen.getByTestId("nl-intent"), "two drafts");
    await user.click(screen.getByTestId("nl-generate"));
    await waitFor(() =>
      expect(screen.getByTestId("nl-attempts").querySelectorAll("li")).toHaveLength(1),
    );
    await user.click(screen.getByTestId("nl-generate"));
    await waitFor(() =>
      expect(screen.getByTestId("nl-attempts").querySelectorAll("li")).toHaveLength(2),
    );

    // Newest first: draft 2 (the one now in the editor) leads, draft 1 follows.
    let items = within(screen.getByTestId("nl-attempts")).getAllByRole("listitem");
    expect(items[0]).toHaveTextContent("draft 2");
    expect(items[1]).toHaveTextContent("draft 1");

    // Neither is judged yet, so both are flagged prominent.
    expect(items[0]).toHaveAttribute("data-unjudged", "true");
    expect(items[1]).toHaveAttribute("data-unjudged", "true");

    // Rating the top draft (draft 2 = original index 1) clears only its highlight.
    await user.click(
      within(screen.getByTestId("nl-verdict-1")).getByRole("button", { name: "👍" }),
    );
    items = within(screen.getByTestId("nl-attempts")).getAllByRole("listitem");
    expect(items[0]).not.toHaveAttribute("data-unjudged");
    expect(items[1]).toHaveAttribute("data-unjudged", "true");
  });

  it("gives the intent description box the doubled height (nl-assist-draft-review-ux)", () => {
    renderEditor();
    // 100% bigger than the former h-16: the box is now h-32.
    expect(screen.getByTestId("nl-intent").className).toContain("h-32");
  });

  describe("in-flight model call lifecycle (panel wiring)", () => {
    /**
     * These pin the CALL SITES, which the hook's own unit tests cannot reach: with the panel wired
     * back to a local AbortController, or its Cancel handler stubbed out, useNlRun stays perfectly
     * correct and the orphaned-request bug returns with a green suite. The mocked transport never
     * settles, so the run is still in flight when we act.
     */
    const startedRun = async () => {
      const user = userEvent.setup();
      chatMock.mockImplementation(() => new Promise<NlChatResult>(() => {}));
      validateMock.mockResolvedValue(VALID);
      const handle = renderEditorHandle();

      await user.type(screen.getByTestId("nl-intent"), "only users");
      await user.click(screen.getByTestId("nl-generate"));
      await waitFor(() => expect(chatMock).toHaveBeenCalledTimes(1));

      // generateChat(config, instance, messages, signal) - the signal is the 4th arg.
      const signal = chatMock.mock.calls[0][3] as AbortSignal;
      expect(signal.aborted).toBe(false);
      return { user, signal, handle };
    };

    it("aborts the request when the editor closes, so it cannot outlive the panel", async () => {
      const { signal, handle } = await startedRun();
      handle.unmount();
      expect(signal.aborted).toBe(true);
    });

    it("aborts the request when the panel's Cancel is clicked", async () => {
      const { user, signal } = await startedRun();
      await user.click(screen.getByTestId("nl-cancel"));
      expect(signal.aborted).toBe(true);
    });
  });
});
