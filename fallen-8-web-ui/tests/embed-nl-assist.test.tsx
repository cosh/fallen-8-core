// MIT License
//
// embed-nl-assist.test.tsx
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

// The embed NL-assist policy (StudioConfig.nlAssist, feature studio-embeddable phase 6).
// The load-bearing property is that the policy holds at the TRANSPORT, not just in the UI:
// a custom config persisted by an earlier session (mode, endpoint, apiKey in localStorage)
// must not be able to route a browser-direct model call out of an instance-only embed, and
// a disabled embed must refuse the call outright even if a caller reaches generateChat.

import { render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { setStudioConfig, StudioConfigContext } from "../src/app/studioConfig";
import {
  DEFAULT_NL_CONFIG,
  resolveNlConfig,
  useNlAssist,
  type NlAssistConfig,
} from "../src/delegate/nl/config";
import { generateChat } from "../src/delegate/nl/generate";
import { NlAssistPanel } from "../src/delegate/nl/NlAssistPanel";
import { NlBackendConfig } from "../src/delegate/nl/NlBackendConfig";
import { PluginNlAssistPanel } from "../src/plugin/nl/PluginNlAssistPanel";
import { SAME_ORIGIN_INSTANCE } from "../src/instances/registry";
import { postChat } from "../src/api/endpoints";

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const mod = await importOriginal<typeof import("../src/api/endpoints")>();
  return { ...mod, postChat: vi.fn() };
});

const CUSTOM_CONFIG: NlAssistConfig = {
  ...DEFAULT_NL_CONFIG,
  mode: "custom",
  endpoint: "http://localhost:11434",
  model: "phi4-mini",
  apiKey: "browser-held-key",
};

const fetchSpy = vi.fn();
vi.stubGlobal("fetch", fetchSpy);

afterEach(() => {
  setStudioConfig({});
  useNlAssist.setState({ config: DEFAULT_NL_CONFIG, leaveNoticeAccepted: false });
  vi.mocked(postChat).mockReset();
  fetchSpy.mockReset();
});

describe("resolveNlConfig (the policy choke point)", () => {
  it("passes a custom config through unchanged when no policy is set", () => {
    setStudioConfig({});
    expect(resolveNlConfig(CUSTOM_CONFIG)).toEqual(CUSTOM_CONFIG);
  });

  it("forces a persisted custom config back to instance mode and drops its key under instance-only", () => {
    setStudioConfig({ nlAssist: "instance-only" });
    const resolved = resolveNlConfig(CUSTOM_CONFIG);
    expect(resolved.mode).toBe("instance");
    expect(resolved.apiKey).toBeUndefined();
  });

  it("leaves an instance config alone under instance-only", () => {
    setStudioConfig({ nlAssist: "instance-only" });
    expect(resolveNlConfig(DEFAULT_NL_CONFIG)).toBe(DEFAULT_NL_CONFIG);
  });
});

describe("generateChat under the embed policy", () => {
  it("refuses outright when the host disabled NL assist, calling neither transport", async () => {
    setStudioConfig({ nlAssist: "disabled" });
    await expect(
      generateChat(CUSTOM_CONFIG, SAME_ORIGIN_INSTANCE, [{ role: "user", content: "hi" }]),
    ).rejects.toThrow(/disabled by the embedding host/);
    expect(postChat).not.toHaveBeenCalled();
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it("routes a persisted custom config through the instance under instance-only (never browser-direct)", async () => {
    setStudioConfig({ nlAssist: "instance-only" });
    vi.mocked(postChat).mockResolvedValue({ content: "drafted", model: "m", stats: null });
    const result = await generateChat(CUSTOM_CONFIG, SAME_ORIGIN_INSTANCE, [
      { role: "user", content: "hi" },
    ]);
    expect(result.content).toBe("drafted");
    expect(postChat).toHaveBeenCalledTimes(1);
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it("still goes browser-direct for a custom config when no policy is set (standalone behavior)", async () => {
    setStudioConfig({});
    fetchSpy.mockResolvedValue(
      new Response(JSON.stringify({ message: { content: "direct" } }), { status: 200 }),
    );
    const result = await generateChat(CUSTOM_CONFIG, SAME_ORIGIN_INSTANCE, [
      { role: "user", content: "hi" },
    ]);
    expect(result.content).toBe("direct");
    expect(postChat).not.toHaveBeenCalled();
  });
});

describe("the NL affordances under the embed policy", () => {
  const panelProps = {
    delegateKind: "VertexFilter" as const,
    currentFragment: "",
    onDraft: () => {},
    validateDraft: async () => null,
    drivingRef: { current: false },
  };

  it("NlAssistPanel renders nothing when the host disabled NL assist", () => {
    const { container } = render(
      <StudioConfigContext.Provider value={{ nlAssist: "disabled" }}>
        <NlAssistPanel {...panelProps} />
      </StudioConfigContext.Provider>,
    );
    expect(container).toBeEmptyDOMElement();
  });

  it("PluginNlAssistPanel renders nothing when the host disabled NL assist", () => {
    const { container } = render(
      <StudioConfigContext.Provider value={{ nlAssist: "disabled" }}>
        <PluginNlAssistPanel
          category="algorithm"
          contract="Path"
          name="X"
          scaffold=""
          currentSource=""
          onDraft={() => {}}
          validateDraft={async () => null}
          drivingRef={{ current: false }}
        />
      </StudioConfigContext.Provider>,
    );
    expect(container).toBeEmptyDOMElement();
  });

  it("NlBackendConfig hides the mode switch and says why under instance-only", () => {
    render(
      <StudioConfigContext.Provider value={{ nlAssist: "instance-only" }}>
        <NlBackendConfig config={DEFAULT_NL_CONFIG} setConfig={() => {}} />
      </StudioConfigContext.Provider>,
    );
    expect(screen.getByTestId("nl-instance-locked")).toBeInTheDocument();
    expect(screen.queryByRole("combobox")).not.toBeInTheDocument();
  });

  it("NlBackendConfig still offers both modes without a policy", () => {
    render(
      <StudioConfigContext.Provider value={{}}>
        <NlBackendConfig config={DEFAULT_NL_CONFIG} setConfig={() => {}} />
      </StudioConfigContext.Provider>,
    );
    expect(screen.queryByTestId("nl-instance-locked")).not.toBeInTheDocument();
    expect(screen.getByRole("combobox")).toBeInTheDocument();
  });
});
