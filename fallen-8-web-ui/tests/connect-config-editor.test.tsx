// MIT License
//
// connect-config-editor.test.tsx
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
import type { ConfigREST, ConfigWriteREST, ConfigWriteSpec, SettingREST } from "../src/api/types";
import { ApiError } from "../src/api/client";

/**
 * Configuration as an EDITOR (features writable-instance-config 5.1 to 5.8 and
 * configuration-surface). The rows now live in a dialog reached from the Connect card, so every case
 * opens it first, and a few cases exist purely because moving them there is what could break them.
 *
 * The properties worth testing are the ones that would silently mislead an operator: a row that
 * cannot be written must say why rather than offering a dead control, a failed write must not erase
 * the surface, the poll must not overwrite what someone is typing, and closing the dialog must not
 * throw away unsaved work.
 */

const getConfigMock = vi.fn<(i: InstanceConfig, s?: AbortSignal) => Promise<ConfigREST | null>>();
const writeConfigMock = vi.fn<(i: InstanceConfig, spec: ConfigWriteSpec) => Promise<ConfigWriteREST>>();
vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    getConfig: (i: InstanceConfig, s?: AbortSignal) => getConfigMock(i, s),
    writeConfig: (i: InstanceConfig, spec: ConfigWriteSpec) => writeConfigMock(i, spec),
  };
});

// Every `poll` the panel has asked useConfig for, newest last. The suspension is a ten second
// interval, which is not something a unit test can wait out, so the option itself is the assertion.
const polls: boolean[] = [];
vi.mock("../src/state/status", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/state/status")>();
  return {
    ...original,
    useConfig: (instance: InstanceConfig, options?: { poll?: boolean }) => {
      polls.push(options?.poll ?? true);
      return original.useConfig(instance, options);
    },
  };
});

function lastPoll(): boolean | undefined {
  return polls[polls.length - 1];
}

import { ConfigurationPanel } from "../src/components/ConfigurationPanel";
import { SAME_ORIGIN_INSTANCE, useRegistry } from "../src/instances/registry";
import { StudioConfigContext } from "../src/app/studioConfig";
import { settingTestId } from "../src/lib/configCatalog";
import { openConfig, selectSection } from "./configSurface";

function setting(overrides: Partial<SettingREST> = {}): SettingREST {
  return {
    key: "Fallen8:Plugins:MaxCount",
    kind: "int",
    tier: "restart",
    applyMode: "restart",
    value: "64",
    source: "default",
    restartPending: false,
    minimum: 1,
    ...overrides,
  };
}

function config(settings: SettingREST[], overrides: Partial<ConfigREST> = {}): ConfigREST {
  return {
    semantic: { embedding: null, chat: null },
    observability: {
      otlpEnabled: false,
      otlpEndpoint: null,
      prometheusEnabled: false,
      prometheusRequireApiKey: false,
      tracingSamplingRatio: 1,
      statisticsElementBudget: 1_000_000,
      statisticsTopN: 20,
    },
    // The editable cases model an instance with both operator acts in place, which is what the server
    // publishes as configWriteEnabled.
    apiKeyRequired: true,
    configWriteEnabled: true,
    settings,
    pendingRestart: [],
    ...overrides,
  };
}

function renderPanel(studio: { lockInstances?: boolean; lockNamespace?: boolean } = {}) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <StudioConfigContext.Provider value={studio}>
        <ConfigurationPanel />
      </StudioConfigContext.Provider>
    </QueryClientProvider>,
  );
}

const PLUGIN_KEY = "Fallen8:Plugins:MaxCount";
const PLUGIN_ROW = settingTestId(PLUGIN_KEY);

beforeEach(() => {
  getConfigMock.mockReset();
  writeConfigMock.mockReset();
  polls.length = 0;
  useRegistry.setState({ instances: [SAME_ORIGIN_INSTANCE], activeId: SAME_ORIGIN_INSTANCE.id });
});

describe("Configuration editor", () => {
  it("writes only the rows that were edited, and sends the value as configuration text", async () => {
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(
      config([setting(), setting({ key: "Fallen8:StoredQueries:MaxCount", value: "256" })]),
    );
    writeConfigMock.mockResolvedValue({ results: [], pendingRestart: [] });
    renderPanel();
    await openConfig(user);

    const field = await screen.findByTestId(PLUGIN_ROW);
    await user.clear(field);
    await user.type(field, "128");

    await user.click(screen.getByTestId("config-save"));

    await waitFor(() => expect(writeConfigMock).toHaveBeenCalledTimes(1));
    expect(writeConfigMock.mock.calls[0][1]).toEqual({
      settings: { "Fallen8:Plugins:MaxCount": "128" },
    });
  });

  it("suspends the ten second poll while there are unsaved edits, so nothing overwrites the field", async () => {
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(config([setting()]));
    renderPanel();
    await openConfig(user);

    const field = await screen.findByTestId(PLUGIN_ROW);
    expect(screen.queryByTestId("config-dirty")).toBeNull();

    await user.clear(field);
    await user.type(field, "128");

    // The card announces the state, and its refresh control becomes a discard rather than staying a
    // plain reload that would silently drop the edit. Both are on the card on purpose: an operator who
    // closed the surface must still be able to see, and undo, unsaved work.
    expect(screen.getByTestId("config-dirty")).toBeInTheDocument();
    expect(screen.getByTestId("config-refresh")).toHaveTextContent("Discard");

    // Close the surface first: Radix puts pointer-events: none on the body while it is open, so a
    // click on the card behind it would be swallowed.
    await user.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByTestId("config-surface")).toBeNull());
    expect(screen.getByTestId("config-dirty")).toBeInTheDocument();

    await user.click(screen.getByTestId("config-refresh"));
    expect(screen.queryByTestId("config-dirty")).toBeNull();
    await openConfig(user);
    await waitFor(() => expect(screen.getByTestId(PLUGIN_ROW)).toHaveValue(64));
  });

  it("keeps the draft when the surface is closed, so unsaved work survives a stray Escape", async () => {
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(config([setting()]));
    renderPanel();
    await openConfig(user);

    const field = await screen.findByTestId(PLUGIN_ROW);
    await user.clear(field);
    await user.type(field, "128");

    await user.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByTestId("config-surface")).toBeNull());

    await openConfig(user);
    // The value typed before the close, not the value the server reports.
    expect(await screen.findByTestId(PLUGIN_ROW)).toHaveValue(128);
    expect(screen.getByTestId("config-save")).toHaveTextContent("Save 1");
  });

  it("keeps the poll suspended while dirty even with the surface closed, and resumes on discard", async () => {
    // The poll suspension has a new way to break: it is driven by the draft, and the draft now
    // outlives the dialog. Someone moving the draft into the dialog would destroy it on close and
    // silently un-suspend the poll, and every other case here would stay green over that.
    //
    // Asserted on the option the panel passes rather than by advancing a clock: the interval is ten
    // seconds, testing-library's waitFor cannot see vitest's fake timers (it polls with its own
    // setInterval), and a real twelve second wait per half has no place in a unit suite.
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(config([setting()]));
    renderPanel();
    await openConfig(user);
    expect(lastPoll()).toBe(true);

    const field = await screen.findByTestId(PLUGIN_ROW);
    await user.clear(field);
    await user.type(field, "128");
    expect(lastPoll()).toBe(false);

    await user.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByTestId("config-surface")).toBeNull());
    expect(screen.getByTestId("config-dirty")).toBeInTheDocument();
    // The closed surface is the case that would regress.
    expect(lastPoll()).toBe(false);

    await user.click(screen.getByTestId("config-refresh"));
    await waitFor(() => expect(lastPoll()).toBe(true));
  });

  it("adds no second subscription to the config query when the surface opens", async () => {
    // Two observers on the same query key would let react-query take the shortest refetch interval,
    // and a card left polling would replace a value under a half-typed field in the open surface.
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(config([setting()]));
    renderPanel();
    await screen.findByTestId("config-configure");
    const beforeOpen = getConfigMock.mock.calls.length;

    await openConfig(user);
    await screen.findByTestId(PLUGIN_ROW);
    expect(getConfigMock.mock.calls.length).toBe(beforeOpen);
  });

  it("renders an environment-locked row read-only and names the variable to remove", async () => {
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(config([setting({ source: "environment" })]));
    renderPanel();
    await openConfig(user);

    const field = await screen.findByTestId(PLUGIN_ROW);
    expect(field).toBeDisabled();
    // The exact double-underscore spelling, because that is what an operator has to go and delete.
    expect(screen.getByTestId(`${PLUGIN_ROW}-env`)).toHaveTextContent("Fallen8__Plugins__MaxCount");
  });

  it("publishes no control for a never-writable key, and gives the rule and the reason instead", async () => {
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(
      config([
        setting({
          key: "Fallen8:Security:ApiKey",
          kind: "string",
          tier: "notWritable",
          applyMode: "never",
          value: undefined,
          valueWithheld: true,
          rule: "R1",
          reason: "Blanking it locks every caller out with no way back in over REST.",
        }),
      ]),
    );
    renderPanel();
    await openConfig(user);
    await selectSection(user, "security");

    const key = settingTestId("Fallen8:Security:ApiKey");
    const reason = await screen.findByTestId(`${key}-reason`);
    expect(reason).toHaveTextContent("R1");
    expect(reason).toHaveTextContent("no way back in over REST");
    // No input at all: an operator must not be able to type into something that cannot be saved. The
    // reason row above proves the section IS on screen, so this is not vacuously true.
    expect(screen.queryByTestId(key)).toBeNull();
  });

  it("shows a failed write inline and keeps the surface, rather than collapsing to unavailable", async () => {
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(config([setting()]));
    writeConfigMock.mockRejectedValue(
      new ApiError(
        409,
        "/config",
        JSON.stringify({ detail: "'Fallen8:Plugins:MaxCount' is declared in the environment" }),
      ),
    );
    renderPanel();
    await openConfig(user);

    const field = await screen.findByTestId(PLUGIN_ROW);
    await user.clear(field);
    await user.type(field, "128");
    await user.click(screen.getByTestId("config-save"));

    const error = await screen.findByTestId("config-settings-error");
    expect(error).toHaveTextContent(/declared in the environment/);
    // The read surface survives: a write failure is not a reason to hide what the instance reports.
    expect(screen.getByTestId("config-surface")).toBeInTheDocument();
    expect(screen.queryByTestId("config-unavailable")).toBeNull();
    expect(screen.getByTestId(PLUGIN_ROW)).toBeInTheDocument();
  });

  it("keeps a refusal visible on the card after the surface is closed", async () => {
    // Otherwise closing the dialog on a 409 leaves a Connect screen that looks like the save landed.
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(config([setting()]));
    writeConfigMock.mockRejectedValue(new ApiError(409, "/config", JSON.stringify({ detail: "refused" })));
    renderPanel();
    await openConfig(user);

    const field = await screen.findByTestId(PLUGIN_ROW);
    await user.clear(field);
    await user.type(field, "128");
    await user.click(screen.getByTestId("config-save"));
    await screen.findByTestId("config-settings-error");

    await user.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByTestId("config-surface")).toBeNull());
    expect(screen.getByTestId("config-settings-error")).toHaveTextContent("refused");
  });

  it("counts the pending-restart set on the card and discloses the values in the surface", async () => {
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(
      config([setting({ value: "128", source: "override", restartPending: true })], {
        pendingRestart: [{ key: PLUGIN_KEY, runningValue: "64", pendingValue: "128" }],
      }),
    );
    renderPanel();

    // The card carries the count sentence and NOT the key list: it is a summary, and the list is what
    // it exists not to be.
    const banner = await screen.findByTestId("config-pending-restart");
    expect(banner).toHaveTextContent(/differs from what this instance started with/);
    expect(banner).not.toHaveTextContent(PLUGIN_KEY);

    await openConfig(user);
    const detail = await screen.findByTestId("config-pending-restart-detail");
    expect(detail).toHaveTextContent(PLUGIN_KEY);
    expect(detail).toHaveTextContent("64");
    expect(detail).toHaveTextContent("128");
  });

  it("offers a Clear only for a row whose stored value is the one in force", async () => {
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(config([setting({ value: "128", source: "override" })]));
    writeConfigMock.mockResolvedValue({ results: [], pendingRestart: [] });
    renderPanel();
    await openConfig(user);

    await user.click(await screen.findByTestId(`${PLUGIN_ROW}-clear`));
    await user.click(screen.getByTestId("config-save"));

    // null is the undo: it removes the stored value rather than writing a new one.
    await waitFor(() => expect(writeConfigMock).toHaveBeenCalledTimes(1));
    expect(writeConfigMock.mock.calls[0][1]).toEqual({
      settings: { "Fallen8:Plugins:MaxCount": null },
    });
  });

  it("is read-only when the server does not accept writes, and says both acts are needed", async () => {
    // The case that used to mislead: an API key IS configured, but the capability is off, so every
    // save would answer 403. The server publishes configWriteEnabled=false and the surface renders
    // read-only instead of offering that save.
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(
      config([setting()], { apiKeyRequired: true, configWriteEnabled: false }),
    );
    renderPanel();
    await openConfig(user);

    expect(await screen.findByTestId(PLUGIN_ROW)).toBeDisabled();
    // The explanation has ONE home, and it is the surface, because a portal is never a descendant of
    // the card's own section element. It sits ABOVE the rows, in the part of the dialog that does not
    // scroll: it used to be in the footer, where someone looking at a disabled control could not see
    // it, and a disabled control that looks live is indistinguishable from a broken one.
    const note = screen.getByTestId("config-read-only-note");
    expect(note).toHaveTextContent(
      /writes need an API key and Fallen8:Security:EnableConfigurationWrite/,
    );
    expect(note.compareDocumentPosition(screen.getByTestId("config-section-pane"))).toBe(
      Node.DOCUMENT_POSITION_FOLLOWING,
    );
    expect(screen.getByTestId("configuration-panel")).not.toHaveTextContent(
      /writes need an API key/,
    );
  });

  it("renders an enum as a select over the values the server allows, and one that opens", async () => {
    // Nothing covered the enum branch, which is how a read-only instance came to look like a broken
    // dropdown: the control had all three options and was simply disabled, with nothing saying so.
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(
      config([
        setting({
          key: "Fallen8:Namespaces:StartupLoadMode",
          kind: "enum",
          value: "Catalog",
          allowedValues: ["Catalog", "All", "DefaultOnly"],
          minimum: undefined,
        }),
      ]),
    );
    renderPanel();
    await openConfig(user);
    await selectSection(user, "namespaces");

    const select = await screen.findByTestId(settingTestId("Fallen8:Namespaces:StartupLoadMode"));
    expect(select).not.toBeDisabled();
    expect([...(select as HTMLSelectElement).options].map((o) => o.value)).toEqual([
      "Catalog",
      "All",
      "DefaultOnly",
    ]);

    await user.selectOptions(select, "All");
    expect(select).toHaveValue("All");
    expect(screen.getByTestId("config-save")).toHaveTextContent("Save 1");
  });

  it("disables the enum on a read-only instance, and says so where the row is", async () => {
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(
      config(
        [
          setting({
            key: "Fallen8:Namespaces:StartupLoadMode",
            kind: "enum",
            value: "Catalog",
            allowedValues: ["Catalog", "All", "DefaultOnly"],
            minimum: undefined,
          }),
        ],
        { apiKeyRequired: false, configWriteEnabled: false },
      ),
    );
    renderPanel();
    await openConfig(user);
    await selectSection(user, "namespaces");

    const select = await screen.findByTestId(settingTestId("Fallen8:Namespaces:StartupLoadMode"));
    expect(select).toBeDisabled();
    // The options are all there; the control just cannot be used. Without the note this reads as a
    // dropdown that does not open.
    expect([...(select as HTMLSelectElement).options]).toHaveLength(3);
    expect(screen.getByTestId("config-read-only-note")).toBeInTheDocument();
  });

  it("treats a missing configWriteEnabled as read-only, because an older server has no write route", async () => {
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(
      config([setting()], { apiKeyRequired: true, configWriteEnabled: undefined }),
    );
    renderPanel();
    await openConfig(user);

    expect(await screen.findByTestId(PLUGIN_ROW)).toBeDisabled();
  });

  it("keeps a blanked numeric field from saving, since the server refuses the whole batch over it", async () => {
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(config([setting()]));
    renderPanel();
    await openConfig(user);

    const field = await screen.findByTestId(PLUGIN_ROW);
    await user.clear(field);

    const save = screen.getByTestId("config-save");
    expect(save).toBeDisabled();
    expect(save).toHaveAttribute("title", expect.stringContaining("Fallen8:Plugins:MaxCount"));
    expect(writeConfigMock).not.toHaveBeenCalled();
  });

  it("gates the editable region on lockInstances, and the namespace policy on lockNamespace too", async () => {
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(
      config([setting(), setting({ key: "Fallen8:Namespaces:LoadOnStartup", kind: "bool", value: "true" })]),
    );

    const locked = renderPanel({ lockInstances: true });
    await openConfig(user);
    await selectSection(user, "ceilings");
    expect(await screen.findByTestId(PLUGIN_ROW)).toBeDisabled();
    locked.unmount();

    // An embed that locked namespace management must not be able to re-plan the host's next boot
    // through the instance-wide startup default either, even though instances are otherwise editable.
    // Both halves matter: the lock narrows to the Fallen8:Namespaces prefix, it does not lock the
    // surface, so each row is asserted in its own section.
    renderPanel({ lockNamespace: true });
    await openConfig(user);
    await selectSection(user, "namespaces");
    const startup = settingTestId("Fallen8:Namespaces:LoadOnStartup");
    await waitFor(() => expect(screen.getByTestId(startup)).toBeDisabled());

    await selectSection(user, "ceilings");
    await waitFor(() => expect(screen.getByTestId(PLUGIN_ROW)).not.toBeDisabled());
  });

  it("keeps working against an instance that publishes no settings at all", async () => {
    // An older server answers without the new fields; the card must read them defensively rather
    // than crashing the Connect screen, and the surface must say so rather than looking broken.
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(config([], { settings: undefined, pendingRestart: undefined }));
    renderPanel();

    expect(await screen.findByTestId("configuration-panel")).toBeInTheDocument();
    expect(screen.queryByTestId("config-pending-restart")).toBeNull();

    await openConfig(user);
    // Asserted with the surface OPEN, so the absent Save means "there is nothing to save" rather
    // than "nothing is rendered".
    expect(screen.getByTestId("config-no-inventory")).toBeInTheDocument();
    expect(screen.queryByTestId("config-save")).toBeNull();
  });

  it("closes the surface and drops the draft when the active instance changes", async () => {
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(config([setting()]));
    renderPanel();
    await openConfig(user);

    const field = await screen.findByTestId(PLUGIN_ROW);
    await user.clear(field);
    await user.type(field, "128");
    expect(screen.getByTestId("config-dirty")).toBeInTheDocument();

    // Otherwise Save would write one instance's intended values into another's configuration, and the
    // open surface would show the wrong instance's rows while the new one is fetched.
    useRegistry.setState({
      instances: [SAME_ORIGIN_INSTANCE, { ...SAME_ORIGIN_INSTANCE, id: "other", name: "other" }],
      activeId: "other",
    });

    await waitFor(() => expect(screen.queryByTestId("config-surface")).toBeNull());
    expect(screen.queryByTestId("config-dirty")).toBeNull();
  });

  it("never lets a filter or a search change whether a row can be edited", async () => {
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(
      config([setting({ source: "environment" }), setting({ key: "Fallen8:StoredQueries:MaxCount", value: "256" })]),
    );
    renderPanel();
    await openConfig(user);
    await selectSection(user, "ceilings");
    expect(await screen.findByTestId(PLUGIN_ROW)).toBeDisabled();

    await user.click(screen.getByTestId("config-filter-environment"));
    await waitFor(() =>
      expect(screen.queryByTestId(settingTestId("Fallen8:StoredQueries:MaxCount"))).toBeNull(),
    );
    expect(screen.getByTestId(PLUGIN_ROW)).toBeDisabled();

    await user.type(screen.getByTestId("config-search"), "maxcount");
    await waitFor(() => expect(screen.getByTestId(PLUGIN_ROW)).toBeDisabled());
  });
});
