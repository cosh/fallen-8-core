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
import { chatWithModel, generateChat, probeEndpoint } from "../src/delegate/nl/generate";
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

  it("chatWithModel refuses under any policy, whoever the caller is (structural, not just generateChat)", async () => {
    setStudioConfig({ nlAssist: "instance-only" });
    await expect(
      chatWithModel(CUSTOM_CONFIG, [{ role: "user", content: "hi" }]),
    ).rejects.toThrow(/not available in this embed/);
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it("the reachability probe is policy-gated too (it carries the apiKey as a header)", async () => {
    setStudioConfig({ nlAssist: "instance-only" });
    await expect(probeEndpoint(CUSTOM_CONFIG)).resolves.toBe(false);
    expect(fetchSpy).not.toHaveBeenCalled();
  });
});

describe("the persisted store under the embed policy (the merge is where it holds)", () => {
  it("rehydrating a persisted custom blob under instance-only yields a clean store, and a settings write re-persists the clean shape", async () => {
    setStudioConfig({ nlAssist: "instance-only" });
    localStorage.setItem(
      "f8.nl-assist",
      JSON.stringify({ state: { config: CUSTOM_CONFIG, leaveNoticeAccepted: true }, version: 2 }),
    );

    await useNlAssist.persist.rehydrate();

    const { config } = useNlAssist.getState();
    expect(config.mode).toBe("instance");
    expect(config.apiKey).toBeUndefined();

    // The write-back drops the key instead of carrying it into the embed's storage.
    useNlAssist.getState().setConfig({ temperature: 0.5 });
    const persisted = JSON.parse(localStorage.getItem("f8.nl-assist")!) as {
      state: { config: NlAssistConfig };
    };
    expect(persisted.state.config.mode).toBe("instance");
    expect(persisted.state.config.apiKey).toBeUndefined();
    localStorage.removeItem("f8.nl-assist");
  });

  it("rehydrating the same blob with no policy keeps the custom config (standalone behavior)", async () => {
    setStudioConfig({});
    localStorage.setItem(
      "f8.nl-assist",
      JSON.stringify({ state: { config: CUSTOM_CONFIG, leaveNoticeAccepted: true }, version: 2 }),
    );

    await useNlAssist.persist.rehydrate();

    const { config } = useNlAssist.getState();
    expect(config.mode).toBe("custom");
    expect(config.apiKey).toBe("browser-held-key");
    localStorage.removeItem("f8.nl-assist");
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

  it("NlBackendConfig never shows custom fields under instance-only, even handed a raw custom config", () => {
    // The mode it renders is derived inside the component, not trusted from the caller: a
    // future call site passing the unresolved store config still cannot show endpoint/key
    // fields inside a locked embed.
    render(
      <StudioConfigContext.Provider value={{ nlAssist: "instance-only" }}>
        <NlBackendConfig config={CUSTOM_CONFIG} setConfig={() => {}} />
      </StudioConfigContext.Provider>,
    );
    expect(screen.getByTestId("nl-instance-locked")).toBeInTheDocument();
    expect(screen.queryByPlaceholderText("http://localhost:11434")).not.toBeInTheDocument();
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

  it("the instance-mode hint names no model, because this form cannot know the server's", () => {
    render(
      <StudioConfigContext.Provider value={{}}>
        <NlBackendConfig config={DEFAULT_NL_CONFIG} setConfig={() => {}} />
      </StudioConfigContext.Provider>,
    );
    const hint = screen.getByTestId("nl-instance-hint");
    expect(hint).not.toHaveTextContent("phi4");
    expect(hint).toHaveTextContent("Fallen8:Chat");
  });
});
