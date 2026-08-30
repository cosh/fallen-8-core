// MIT License
//
// config-model-picker.test.tsx
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

import { useCallback, useState } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { ApiError } from "../src/api/client";
import type { InstanceConfig } from "../src/instances/types";
import type {
  ChatModelsREST,
  ChatProviderStatsREST,
  ConfigREST,
  ObservabilityConfigREST,
  SettingREST,
} from "../src/api/types";

/**
 * The chat model picker on the configuration surface (feature chat-model-catalog, FR-4 and
 * decisions 7 and 8).
 *
 * Two properties carry the whole feature and both are invisible in a screenshot. First, the read is
 * a credentialed fan-out on the server's side, so it must not happen from merely LOOKING at
 * configuration: every gate gets its own case. Second, the list only ever OFFERS - a name the backend
 * resolves but does not catalogue (f8-delegate:latest is the shipped example) must stay typeable, so
 * the cases assert what free text does rather than only what the list contains.
 *
 * Most of these render the surface directly, because what is under test is the surface's own
 * contract: which row is offered what, and when nothing is fetched at all. The LAST case renders the
 * Connect card instead, because a surface-level case cannot see whether the one mount that ships
 * passes the chat state at all, and a picker nothing switches on is a feature that is not there.
 */

const getChatModelsMock =
  vi.fn<(i: InstanceConfig, s?: AbortSignal) => Promise<ChatModelsREST | null>>();
const getConfigMock = vi.fn<(i: InstanceConfig, s?: AbortSignal) => Promise<ConfigREST | null>>();
vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    getChatModels: (i: InstanceConfig, s?: AbortSignal) => getChatModelsMock(i, s),
    getConfig: (i: InstanceConfig, s?: AbortSignal) => getConfigMock(i, s),
  };
});

import { ConfigurationSurface, type ConfigurationSurfaceProps } from "../src/components/ConfigurationSurface";
import { ConfigurationPanel } from "../src/components/ConfigurationPanel";
import { SAME_ORIGIN_INSTANCE, useRegistry } from "../src/instances/registry";
import { settingTestId } from "../src/lib/configCatalog";
import { SettingRow, type SettingSuggestion } from "../src/components/SettingRow";
import { openConfig, selectSection } from "./configSurface";

const OLLAMA_KEY = "Fallen8:Chat:Ollama:Model";
const NAHIL_KEY = "Fallen8:Chat:Nahil:Model";
const OPENAI_KEY = "Fallen8:Chat:OpenAI:Model";
const ANTHROPIC_KEY = "Fallen8:Chat:Anthropic:Model";
const EMBEDDING_KEY = "Fallen8:Embedding:Ollama:Model";
const CHAT_ENABLED_KEY = "Fallen8:Chat:Enabled";
const CEILING_KEY = "Fallen8:Plugins:MaxCount";

const OBSERVABILITY: ObservabilityConfigREST = {
  otlpEnabled: false,
  otlpEndpoint: null,
  prometheusEnabled: false,
  prometheusRequireApiKey: false,
  tracingSamplingRatio: 1,
  statisticsElementBudget: 1_000_000,
  statisticsTopN: 20,
};

function setting(key: string, overrides: Partial<SettingREST> = {}): SettingREST {
  return {
    key,
    kind: "string",
    tier: "restart",
    applyMode: "restart",
    value: "",
    source: "default",
    restartPending: false,
    ...overrides,
  };
}

/**
 * The shipped descriptors this picker reads, in the shapes Fallen8SettingCatalog publishes them:
 * every chat model key is a writable `string` on the Restart tier, the embedding model key is
 * NotWritable under R3 with its value withheld, and there is a section ahead of Chat so the surface
 * does not land on the Chat pane by itself.
 */
function inventory(): SettingREST[] {
  return [
    setting(CEILING_KEY, { kind: "int", value: "64", minimum: 1 }),
    setting(EMBEDDING_KEY, {
      tier: "notWritable",
      applyMode: "never",
      value: undefined,
      valueWithheld: true,
      rule: "R3",
      reason: "the model name is the identity stamp beside every vector already stored",
    }),
    setting("Fallen8:Chat:Backend", {
      kind: "enum",
      value: "Nahil",
      allowedValues: ["Ollama", "Nahil", "OpenAI", "Anthropic"],
    }),
    setting(OLLAMA_KEY, { value: "phi4-mini:latest" }),
    setting(NAHIL_KEY, { value: "phi4-f8-mini:latest" }),
    setting(OPENAI_KEY, { value: "gpt-4o-mini" }),
    setting(ANTHROPIC_KEY, { value: "claude-sonnet-4" }),
  ];
}

/** The inventory with one row patched, so a case can state the one fact it is about. */
function inventoryWith(key: string, overrides: Partial<SettingREST>): SettingREST[] {
  return inventory().map((entry) => (entry.key === key ? { ...entry, ...overrides } : entry));
}

const CHAT_ON: ChatProviderStatsREST = {
  enabled: true,
  backend: "Nahil",
  model: "phi4-f8-mini:latest",
  loaded: false,
};

/**
 * The live Nahil catalog (probed 2026-08-30), trimmed to the interesting rows: two completion models
 * of different classes and warm state, one embedding model Studio must drop, and one entry whose
 * capability the backend did not report.
 */
const CATALOG: ChatModelsREST = {
  backend: "Nahil",
  models: [
    { name: "phi4-f8-mini:latest", capability: "completion", available: true, class: "S1" },
    { name: "phi4-f8:latest", capability: "completion", available: false, class: "S2" },
    { name: "bge-m3:latest", capability: "embedding", available: true, class: "C2" },
    { name: "unlabelled:latest", capability: null, available: null, class: null },
  ],
};

type Overrides = Partial<Omit<ConfigurationSurfaceProps, "draft" | "onChange" | "onClear">>;

/**
 * Owns the draft the way the Connect card does, because a picker that could not be typed over would
 * pass every assertion about its list and still be useless. The callbacks are stable on purpose: the
 * rows are memoised on exactly that.
 */
function Host({ settings, ...rest }: Overrides & { settings: readonly SettingREST[] }) {
  const [draft, setDraft] = useState<Record<string, string | null>>({});
  const onChange = useCallback(
    (key: string, value: string) => setDraft((current) => ({ ...current, [key]: value })),
    [],
  );
  const onClear = useCallback(
    (key: string) => setDraft((current) => ({ ...current, [key]: null })),
    [],
  );
  return (
    <ConfigurationSurface
      open
      onClose={() => {}}
      instanceName="local"
      pendingRestart={[]}
      observability={OBSERVABILITY}
      dirtyCount={Object.keys(draft).length}
      onSave={() => {}}
      saving={false}
      writeError={null}
      writesAllowed
      editable
      isRowDisabled={() => false}
      chat={CHAT_ON}
      // The surface is entirely prop-driven, this included: it no longer reads the registry, so the
      // instance the catalog read addresses arrives the same way every other fact about it does.
      // Before {...rest} so a case can still override or blank it.
      instance={SAME_ORIGIN_INSTANCE}
      {...rest}
      settings={settings}
      draft={draft}
      onChange={onChange}
      onClear={onClear}
    />
  );
}

function renderSurface(overrides: Overrides & { settings?: readonly SettingREST[] } = {}, retry = 0) {
  const client = new QueryClient({ defaultOptions: { queries: { retry } } });
  const { settings = inventory(), ...rest } = overrides;
  return render(
    <QueryClientProvider client={client}>
      <Host settings={settings} {...rest} />
    </QueryClientProvider>,
  );
}

function row(key: string): HTMLInputElement {
  return screen.getByTestId(settingTestId(key)) as HTMLInputElement;
}

/** Whatever the row's datalist offers, in the order it offers it, with the label as rendered. */
function offered(key: string): { value: string; label: string | null }[] {
  const options = document.querySelectorAll<HTMLOptionElement>(
    `#${settingTestId(key)}-options option`,
  );
  return [...options].map((option) => ({
    value: option.value,
    label: option.getAttribute("label"),
  }));
}

/**
 * Asserts the read count STOPS at `calls`. Written as a wait that has to TIME OUT rather than as a
 * bare count check: react-query starts a fetch a tick after the render that enables it, so a
 * synchronous assertion would pass even on a picker that fetched unconditionally.
 *
 * The callback returns nothing on purpose. Handing waitFor an expression makes it resolve WITH a chai
 * assertion object, which pretty-format cannot print: the failure then arrives as a suite-level
 * "Invalid Chai property: $$typeof" naming no test at all, and the next fan-out regression would be
 * unreadable.
 */
async function expectNoMoreCatalogReads(calls: number): Promise<void> {
  await expect(
    waitFor(
      () => {
        expect(getChatModelsMock.mock.calls.length).toBeGreaterThan(calls);
      },
      { timeout: 150 },
    ),
  ).rejects.toThrow();
}

/** No catalog read at all, which is what every gate in FR-4 is there to guarantee. */
async function expectNoCatalogRead(): Promise<void> {
  await expectNoMoreCatalogReads(0);
}

beforeEach(() => {
  getChatModelsMock.mockReset();
  getConfigMock.mockReset();
  useRegistry.setState({ instances: [SAME_ORIGIN_INSTANCE], activeId: SAME_ORIGIN_INSTANCE.id });
});

describe("Chat model picker", () => {
  it("offers the running backend's completion models on that backend's model row", async () => {
    const user = userEvent.setup();
    getChatModelsMock.mockResolvedValue(CATALOG);
    renderSurface();

    await selectSection(user, "chat");
    await waitFor(() => expect(getChatModelsMock).toHaveBeenCalledTimes(1));
    await waitFor(() =>
      expect(row(NAHIL_KEY)).toHaveAttribute("list", `${settingTestId(NAHIL_KEY)}-options`),
    );

    // Studio filters, the route does not: the embedding model is gone, the entry whose capability the
    // backend never reported stays, and the server's order is preserved.
    expect(offered(NAHIL_KEY)).toEqual([
      { value: "phi4-f8-mini:latest", label: "S1 · warm" },
      { value: "phi4-f8:latest", label: "S2 · cold start" },
      { value: "unlabelled:latest", label: null },
    ]);
    expect(getChatModelsMock).toHaveBeenCalledWith(SAME_ORIGIN_INSTANCE, expect.anything());
  });

  it("offers nothing on the other backends' model rows", async () => {
    const user = userEvent.setup();
    getChatModelsMock.mockResolvedValue(CATALOG);
    renderSurface();

    await selectSection(user, "chat");
    await waitFor(() => expect(row(NAHIL_KEY)).toHaveAttribute("list"));

    for (const key of [OLLAMA_KEY, OPENAI_KEY, ANTHROPIC_KEY]) {
      expect(row(key), key).not.toHaveAttribute("list");
      expect(offered(key), key).toEqual([]);
    }
    // One row in the whole surface, so exactly one datalist exists in it.
    expect(document.querySelectorAll("datalist")).toHaveLength(1);
  });

  it("never offers a picker on an embedding model row, even with both panes on screen", async () => {
    const user = userEvent.setup();
    getChatModelsMock.mockResolvedValue(CATALOG);
    renderSurface();

    // The names are fetched from the Chat section, where the operator asked for them...
    await selectSection(user, "chat");
    await waitFor(() => expect(row(NAHIL_KEY)).toHaveAttribute("list"));
    // ...and only then can a search put both panes on screen at once, which is the one way an
    // embedding model row could ever have been reached by mistake.
    await user.type(screen.getByTestId("config-search"), ":model");
    await screen.findByTestId(`${settingTestId(EMBEDDING_KEY)}-reason`);

    // R3: the row is not writable at all, so it renders its reason and no control to offer anything to.
    expect(screen.queryByTestId(settingTestId(EMBEDDING_KEY))).toBeNull();
    expect(offered(EMBEDDING_KEY)).toEqual([]);
    // Names already in hand stay beside the row they belong to, and only that row: a search narrows
    // the pane, it does not take the list away and it does not move it.
    expect(row(NAHIL_KEY)).toHaveAttribute("list", `${settingTestId(NAHIL_KEY)}-options`);
    expect(document.querySelectorAll("datalist")).toHaveLength(1);
    expect(getChatModelsMock).toHaveBeenCalledTimes(1);
  });

  it("follows the RUNNING backend, not the stored one waiting for a restart", async () => {
    const user = userEvent.setup();
    getChatModelsMock.mockResolvedValue(CATALOG);
    // The operator has written Backend=Ollama; it takes effect at the next boot. Until then the
    // catalog answers for Nahil, so Nahil's row is the one that can be offered names.
    renderSurface({
      settings: inventoryWith("Fallen8:Chat:Backend", { value: "Ollama", restartPending: true }),
      pendingRestart: [
        { key: "Fallen8:Chat:Backend", runningValue: "Nahil", pendingValue: "Ollama" },
      ],
    });

    await selectSection(user, "chat");
    await waitFor(() => expect(row(NAHIL_KEY)).toHaveAttribute("list"));
    expect(row(OLLAMA_KEY)).not.toHaveAttribute("list");
  });

  it("keeps a typed name that is not in the list, and validates nothing", async () => {
    const user = userEvent.setup();
    getChatModelsMock.mockResolvedValue(CATALOG);
    renderSurface();

    await selectSection(user, "chat");
    await waitFor(() => expect(row(NAHIL_KEY)).toHaveAttribute("list"));

    // The shipped example of a name the backend resolves and does not catalogue.
    await user.clear(row(NAHIL_KEY));
    await user.type(row(NAHIL_KEY), "f8-delegate:latest");

    expect(row(NAHIL_KEY)).toHaveValue("f8-delegate:latest");
    expect(row(NAHIL_KEY)).toBeValid();
    expect(row(NAHIL_KEY)).not.toHaveAttribute("pattern");
    expect(row(NAHIL_KEY)).not.toHaveAttribute("aria-invalid");
    // Still a draft the operator can save, and the list is still on offer beside it.
    expect(screen.getByTestId("config-save")).toBeEnabled();
    expect(offered(NAHIL_KEY).map((option) => option.value)).toContain("phi4-f8-mini:latest");
  });

  it("offers nothing when the backend catalogues only embedding models", async () => {
    const user = userEvent.setup();
    getChatModelsMock.mockResolvedValue({
      backend: "Nahil",
      models: [{ name: "bge-m3:latest", capability: "embedding", available: true, class: "C2" }],
    });
    renderSurface();

    await selectSection(user, "chat");
    await waitFor(() => expect(getChatModelsMock).toHaveBeenCalledTimes(1));

    // Nothing to offer is not a failure: the row stays a plain input and says nothing.
    expect(row(NAHIL_KEY)).not.toHaveAttribute("list");
    expect(document.querySelectorAll("datalist")).toHaveLength(0);
    expect(screen.queryByTestId(`${settingTestId(NAHIL_KEY)}-note`)).toBeNull();
  });

  it("reads the catalog once per section visit, not once per pane render", async () => {
    const user = userEvent.setup();
    getChatModelsMock.mockResolvedValue(CATALOG);
    renderSurface();

    await selectSection(user, "chat");
    await waitFor(() => expect(row(NAHIL_KEY)).toHaveAttribute("list"));
    await selectSection(user, "ceilings");
    await waitFor(() => expect(screen.queryByTestId(settingTestId(NAHIL_KEY))).toBeNull());
    await selectSection(user, "chat");
    await waitFor(() => expect(row(NAHIL_KEY)).toHaveAttribute("list"));

    expect(getChatModelsMock).toHaveBeenCalledTimes(1);
    expect(offered(NAHIL_KEY)).toHaveLength(3);
  });

  it("reads nothing when a search keystroke merely surfaces the Chat pane", async () => {
    const user = userEvent.setup();
    getChatModelsMock.mockResolvedValue(CATALOG);
    renderSurface();

    // ONE character, typed by an operator hunting for the Fallen8:Durability keys. A search spans
    // every section and matches a key, its exclusion rule and its reason, so "d" surfaces the Chat
    // pane - which is not the same thing as asking for the model list. A read that carries the
    // operator's credential to their own backend must not fall out of a keystroke.
    await user.type(screen.getByTestId("config-search"), "d");
    await screen.findByTestId(settingTestId(NAHIL_KEY));

    await expectNoCatalogRead();
    expect(row(NAHIL_KEY)).not.toHaveAttribute("list");
    // Withheld is not the same as broken. Search is the affordance for someone who does not know
    // which section a key is in, so the row says where its list comes from rather than silently
    // rendering as a bare field, which is the same "one line saying why" the refusal path gives.
    expect(screen.getByTestId(`${settingTestId(NAHIL_KEY)}-note`)).toHaveTextContent(
      /open the chat section/i,
    );
    // And it stays typable: withholding the list must never withhold the control.
    await user.clear(row(NAHIL_KEY));
    await user.type(row(NAHIL_KEY), "f8-delegate:latest");
    expect(row(NAHIL_KEY)).toHaveValue("f8-delegate:latest");
  });

  it("does not fan out again as the pane flips out and back in after a refusal", async () => {
    const user = userEvent.setup();
    getChatModelsMock.mockRejectedValue(
      new ApiError(503, "http://localhost:5000/api/v0.1/chat/models", ""),
    );
    renderSurface();

    await selectSection(user, "chat");
    await screen.findByTestId(`${settingTestId(NAHIL_KEY)}-note`);
    expect(getChatModelsMock).toHaveBeenCalledTimes(1);

    // A refused read holds no data, so react-query counts it stale whatever staleTime says and
    // fetches again the moment the gate re-opens. One refusal is one refusal: leaving the section and
    // coming back, or typing and clearing a search, must not spend another fan-out on the same
    // backend. The note beside the row is what says the list is unavailable.
    await selectSection(user, "ceilings");
    await selectSection(user, "chat");
    await selectSection(user, "ceilings");
    await selectSection(user, "chat");
    await user.type(screen.getByTestId("config-search"), "d");
    await user.clear(screen.getByTestId("config-search"));

    await screen.findByTestId(`${settingTestId(NAHIL_KEY)}-note`);
    await expectNoMoreCatalogReads(1);
  });

  it("reads nothing while another section is open", async () => {
    getChatModelsMock.mockResolvedValue(CATALOG);
    renderSurface();

    // The surface lands on the first section with rows, which is not Chat.
    await screen.findByTestId(settingTestId(CEILING_KEY));
    await expectNoCatalogRead();
    expect(screen.queryByTestId(settingTestId(NAHIL_KEY))).toBeNull();
  });

  it("reads nothing when the active filter leaves the model row off screen", async () => {
    const user = userEvent.setup();
    getChatModelsMock.mockResolvedValue(CATALOG);
    // Fallen8:Chat:Enabled is never writable (R5, value withheld), so "not writable" leaves the Chat
    // pane with a row on screen - just not one a list of names could ever be typed into.
    renderSurface({
      settings: [
        ...inventory(),
        setting(CHAT_ENABLED_KEY, {
          kind: "bool",
          tier: "notWritable",
          applyMode: "never",
          value: undefined,
          valueWithheld: true,
          rule: "R5",
          reason: "turning the chat gateway on is a capability the operator opted out of",
        }),
      ],
    });

    await user.click(screen.getByTestId("config-filter-notWritable"));
    await selectSection(user, "chat");
    // The pane is genuinely rendering the section, so this is not a case of nothing being there.
    await screen.findByTestId(`${settingTestId(CHAT_ENABLED_KEY)}-reason`);

    // Nothing on screen could consume a list of names, so no credentialed fan-out happens.
    expect(screen.queryByTestId(settingTestId(NAHIL_KEY))).toBeNull();
    await expectNoCatalogRead();
  });

  it("reads nothing when the chat gateway is off", async () => {
    const user = userEvent.setup();
    getChatModelsMock.mockResolvedValue(CATALOG);
    renderSurface({ chat: { ...CHAT_ON, enabled: false } });

    await selectSection(user, "chat");
    await screen.findByTestId(settingTestId(NAHIL_KEY));
    await expectNoCatalogRead();
    expect(row(NAHIL_KEY)).not.toHaveAttribute("list");
  });

  it("reads nothing when the instance reports no chat state at all", async () => {
    const user = userEvent.setup();
    getChatModelsMock.mockResolvedValue(CATALOG);
    renderSurface({ chat: null });

    await selectSection(user, "chat");
    await screen.findByTestId(settingTestId(NAHIL_KEY));
    await expectNoCatalogRead();
    expect(row(NAHIL_KEY)).not.toHaveAttribute("list");
  });

  it("reads nothing when the instance refuses configuration writes", async () => {
    const user = userEvent.setup();
    getChatModelsMock.mockResolvedValue(CATALOG);
    renderSurface({ writesAllowed: false });

    await selectSection(user, "chat");
    await screen.findByTestId("config-read-only-note");
    await expectNoCatalogRead();
    expect(row(NAHIL_KEY)).not.toHaveAttribute("list");
  });

  it("reads nothing when an embed host locked the instance, row left enabled or not", async () => {
    const user = userEvent.setup();
    getChatModelsMock.mockResolvedValue(CATALOG);
    // lockInstances on its own: the surface refuses to spend the operator's credential on names
    // nothing here could save, even where the individual row was left alone. Deliberately NOT
    // combined with the per-row lock below - together they cover each other, and either gate could
    // then be deleted with this suite still green.
    renderSurface({ editable: false, isRowDisabled: () => false });

    await selectSection(user, "chat");
    await screen.findByTestId(settingTestId(NAHIL_KEY));
    await expectNoCatalogRead();
    expect(row(NAHIL_KEY)).not.toHaveAttribute("list");
    // The Save button is what the instance lock takes away, so there is nowhere for a picked name
    // to go.
    expect(screen.queryByTestId("config-save")).toBeNull();
  });

  it("reads nothing when the lock narrows to this row alone", async () => {
    const user = userEvent.setup();
    getChatModelsMock.mockResolvedValue(CATALOG);
    // The other arm: the surface is editable (Save is offered for other rows) and only THIS row is
    // locked, which is what the namespace lock does to a prefix.
    renderSurface({ editable: true, isRowDisabled: () => true });

    await selectSection(user, "chat");
    await screen.findByTestId(settingTestId(NAHIL_KEY));
    await expectNoCatalogRead();
    expect(row(NAHIL_KEY)).toBeDisabled();
    expect(row(NAHIL_KEY)).not.toHaveAttribute("list");
  });

  it("reads nothing when a rule made the model row never-writable", async () => {
    const user = userEvent.setup();
    getChatModelsMock.mockResolvedValue(CATALOG);
    // The tier clause on its own. Not hypothetical in kind: the catalog already publishes every
    // Fallen8:Embedding:*:Model as notWritable under R3, and this row's Endpoint/ApiKey siblings
    // under R4/R8, so a chat model key joining them is one catalog edit away. Such a row renders its
    // reason and NO input, and the credential must not be spent on names nothing could consume.
    renderSurface({ settings: inventoryWith(NAHIL_KEY, { tier: "notWritable", rule: "R3", reason: "pinned by a rule" }) });

    await selectSection(user, "chat");
    await screen.findByTestId(`${settingTestId(NAHIL_KEY)}-reason`);
    await expectNoCatalogRead();
    expect(screen.queryByTestId(settingTestId(NAHIL_KEY))).toBeNull();
  });

  it("reads nothing when the model descriptor is not a string row", async () => {
    const user = userEvent.setup();
    getChatModelsMock.mockResolvedValue(CATALOG);
    // The kind clause on its own: a datalist belongs to a text input, so a descriptor that arrives
    // as another kind gets no picker rather than a combobox grafted onto the wrong control.
    renderSurface({ settings: inventoryWith(NAHIL_KEY, { kind: "int" }) });

    await selectSection(user, "chat");
    await screen.findByTestId(settingTestId(NAHIL_KEY));
    await expectNoCatalogRead();
    expect(row(NAHIL_KEY)).not.toHaveAttribute("list");
  });

  it("reads nothing when the environment declares the model, and says so instead", async () => {
    const user = userEvent.setup();
    getChatModelsMock.mockResolvedValue(CATALOG);
    renderSurface({ settings: inventoryWith(NAHIL_KEY, { source: "environment" }) });

    await selectSection(user, "chat");
    await screen.findByTestId(`${settingTestId(NAHIL_KEY)}-env`);
    await expectNoCatalogRead();
    expect(row(NAHIL_KEY)).not.toHaveAttribute("list");
  });

  it("degrades to a plain input with one line saying why, and still takes a typed name", async () => {
    const user = userEvent.setup();
    getChatModelsMock.mockRejectedValue(
      new ApiError(
        503,
        "http://localhost:5000/api/v0.1/chat/models",
        JSON.stringify({ detail: "the chat backend did not answer" }),
      ),
    );
    // A client that retries by default, to pin the read's own retry: 0 - one refusal must not fan out
    // to the operator's backend a second time.
    renderSurface({}, 3);

    await selectSection(user, "chat");
    const note = await screen.findByTestId(`${settingTestId(NAHIL_KEY)}-note`);

    expect(note).toHaveTextContent("the chat backend did not answer");
    expect(note).toHaveTextContent("type the name");
    // The reason, and never where the instance dialled: the URL is not the operator's business here.
    expect(note.textContent).not.toContain("localhost");
    expect(row(NAHIL_KEY)).not.toHaveAttribute("list");
    expect(document.querySelectorAll("datalist")).toHaveLength(0);
    expect(getChatModelsMock).toHaveBeenCalledTimes(1);

    await user.clear(row(NAHIL_KEY));
    await user.type(row(NAHIL_KEY), "phi4-f8:latest");
    expect(row(NAHIL_KEY)).toHaveValue("phi4-f8:latest");
    expect(screen.getByTestId("config-save")).toBeEnabled();
  });

  it("names the failure honestly when there was no answer to read", async () => {
    const user = userEvent.setup();
    getChatModelsMock.mockRejectedValue(new TypeError("Failed to fetch"));
    renderSurface();

    await selectSection(user, "chat");
    const note = await screen.findByTestId(`${settingTestId(NAHIL_KEY)}-note`);
    expect(note).toHaveTextContent("Failed to fetch");
  });

  it("leaves every string row exactly as it was when there is nothing to offer", async () => {
    const user = userEvent.setup();
    getChatModelsMock.mockResolvedValue(CATALOG);
    renderSurface({ chat: undefined });

    await selectSection(user, "chat");
    const plain = await screen.findByTestId(settingTestId(NAHIL_KEY));

    expect(plain).toHaveAttribute("type", "text");
    expect(plain).toHaveAttribute("id", settingTestId(NAHIL_KEY));
    expect(plain).toHaveClass("input", "w-auto");
    expect(plain).not.toHaveAttribute("list");
    expect(plain).toHaveValue("phi4-f8-mini:latest");
    expect(screen.queryByTestId(`${settingTestId(NAHIL_KEY)}-note`)).toBeNull();
    expect(document.querySelectorAll("datalist")).toHaveLength(0);
    await expectNoCatalogRead();
  });
});

/**
 * The mount that SHIPS (FR-4). Every case above hands the surface its `chat` prop, and that prop is
 * the whole switch for the picker: with it supplied by the test, all of them pass on a Connect card
 * that passes none, and the feature is then absent from the app with the suite green. These two
 * render the card and supply nothing but the answers a server gives.
 */
describe("Chat model picker as the Connect card mounts it", () => {
  function panelConfig(chat: ChatProviderStatsREST): ConfigREST {
    return {
      semantic: { chat },
      observability: OBSERVABILITY,
      apiKeyRequired: true,
      // Both operator acts are in place, which is what makes the rows writable at all.
      configWriteEnabled: true,
      settings: inventory(),
      pendingRestart: [],
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

  it("activates the picker with no chat state supplied by the test", async () => {
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(panelConfig(CHAT_ON));
    getChatModelsMock.mockResolvedValue(CATALOG);
    renderPanel();

    await openConfig(user);
    await selectSection(user, "chat");

    await waitFor(() => expect(getChatModelsMock).toHaveBeenCalledTimes(1));
    await waitFor(() =>
      expect(row(NAHIL_KEY)).toHaveAttribute("list", `${settingTestId(NAHIL_KEY)}-options`),
    );
    expect(offered(NAHIL_KEY).map((option) => option.value)).toEqual([
      "phi4-f8-mini:latest",
      "phi4-f8:latest",
      "unlabelled:latest",
    ]);
  });

  it("passes the gateway the instance reports, not a stand-in for it", async () => {
    const user = userEvent.setup();
    // GET /config says Ollama is SERVING while the inventory's Fallen8:Chat:Backend says Nahil is
    // stored, which is the window after a backend write and before the restart. A card passing a
    // hand-made chat block, or reading the backend off the descriptor, would offer the wrong row.
    getConfigMock.mockResolvedValue(
      panelConfig({ ...CHAT_ON, backend: "Ollama", model: "phi4-mini:latest" }),
    );
    getChatModelsMock.mockResolvedValue(CATALOG);
    renderPanel();

    await openConfig(user);
    await selectSection(user, "chat");

    await waitFor(() => expect(row(OLLAMA_KEY)).toHaveAttribute("list"));
    expect(row(NAHIL_KEY)).not.toHaveAttribute("list");
  });
});

/**
 * The affordance itself, one level below the surface: SettingRow's `suggestions` prop is generic and
 * the next feature that wants offered values will reach for it, so its refusals are pinned here
 * rather than only through the picker that happens to be its first caller.
 */
describe("Setting row suggestions", () => {
  function renderRow(
    overrides: Partial<SettingREST> = {},
    extra: { suggestions?: SettingSuggestion[]; note?: string } = {},
  ): HTMLInputElement {
    render(
      <SettingRow
        setting={setting(NAHIL_KEY, overrides)}
        onChange={() => {}}
        onClear={() => {}}
        {...extra}
      />,
    );
    return row(NAHIL_KEY);
  }

  it("offers nothing on a row that is not a string, however it is called", () => {
    // `array` shares the string branch's control and the server never marks one writable, so a list
    // of values there could only ever be an offer nothing can accept.
    const input = renderRow({ kind: "array", value: "a,b" }, { suggestions: [{ value: "a" }] });
    expect(input).not.toHaveAttribute("list");
    expect(document.querySelectorAll("datalist")).toHaveLength(0);
  });

  it("offers nothing when the list is empty, and keeps the plain control", () => {
    const input = renderRow({}, { suggestions: [] });
    expect(input).not.toHaveAttribute("list");
    expect(input).toHaveClass("input", "w-auto");
    expect(document.querySelectorAll("datalist")).toHaveLength(0);
  });

  it("labels an option only when something is known about it", () => {
    const input = renderRow(
      {},
      { suggestions: [{ value: "a:latest", label: "S1 · warm" }, { value: "b:latest" }] },
    );
    expect(input).toHaveAttribute("list", `${settingTestId(NAHIL_KEY)}-options`);
    expect(offered(NAHIL_KEY)).toEqual([
      { value: "a:latest", label: "S1 · warm" },
      { value: "b:latest", label: null },
    ]);
  });

  it("says nothing under a row that was given no note", () => {
    renderRow({}, { suggestions: [{ value: "a:latest" }] });
    expect(screen.queryByTestId(`${settingTestId(NAHIL_KEY)}-note`)).toBeNull();
  });

  it("keeps the note under the control when the caller could offer nothing", () => {
    renderRow({}, { note: "No model list to offer (503); type the name." });
    expect(screen.getByTestId(`${settingTestId(NAHIL_KEY)}-note`)).toHaveTextContent(
      "type the name",
    );
    expect(row(NAHIL_KEY)).not.toHaveAttribute("list");
  });
});
