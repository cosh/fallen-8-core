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
 * Connect · Configuration as an EDITOR (feature writable-instance-config 5.1 to 5.8).
 *
 * The panel is the codebase's first dirty-state form, and the properties worth testing are the ones
 * that would silently mislead an operator: a row that cannot be written must say why rather than
 * offering a dead control, a failed write must not erase the panel, and the poll must not overwrite
 * what someone is typing.
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

import { ConfigurationPanel } from "../src/components/ConfigurationPanel";
import { SAME_ORIGIN_INSTANCE, useRegistry } from "../src/instances/registry";
import { StudioConfigContext } from "../src/app/studioConfig";
import { settingTestId } from "../src/components/SettingRow";

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
    // A write needs a key configured server-side, so the editable cases say so.
    apiKeyRequired: true,
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

const PLUGIN_ROW = settingTestId("Fallen8:Plugins:MaxCount");

beforeEach(() => {
  getConfigMock.mockReset();
  writeConfigMock.mockReset();
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

    const field = await screen.findByTestId(PLUGIN_ROW);
    expect(screen.queryByTestId("config-dirty")).toBeNull();

    await user.clear(field);
    await user.type(field, "128");

    // The panel announces the state, and the refresh control becomes a discard rather than staying a
    // plain reload that would silently drop the edit.
    expect(screen.getByTestId("config-dirty")).toBeInTheDocument();
    expect(screen.getByTestId("config-refresh")).toHaveTextContent("Discard");

    await user.click(screen.getByTestId("config-refresh"));
    expect(screen.queryByTestId("config-dirty")).toBeNull();
    await waitFor(() => expect(screen.getByTestId(PLUGIN_ROW)).toHaveValue(64));
  });

  it("renders an environment-locked row read-only and names the variable to remove", async () => {
    getConfigMock.mockResolvedValue(config([setting({ source: "environment" })]));
    renderPanel();

    const field = await screen.findByTestId(PLUGIN_ROW);
    expect(field).toBeDisabled();
    // The exact double-underscore spelling, because that is what an operator has to go and delete.
    expect(screen.getByTestId(PLUGIN_ROW).closest("div")?.parentElement).toHaveTextContent(
      "Fallen8__Plugins__MaxCount",
    );
  });

  it("publishes no control for a never-writable key, and gives the rule and the reason instead", async () => {
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

    const key = settingTestId("Fallen8:Security:ApiKey");
    const reason = await screen.findByTestId(`${key}-reason`);
    expect(reason).toHaveTextContent("R1");
    expect(reason).toHaveTextContent("no way back in over REST");
    // No input at all: an operator must not be able to type into something that cannot be saved.
    expect(screen.queryByTestId(key)).toBeNull();
  });

  it("shows a failed write inline and keeps the panel, rather than collapsing to unavailable", async () => {
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

    const field = await screen.findByTestId(PLUGIN_ROW);
    await user.clear(field);
    await user.type(field, "128");
    await user.click(screen.getByTestId("config-save"));

    const error = await screen.findByTestId("config-settings-error");
    expect(error).toHaveTextContent(/declared in the environment/);
    // The read surface survives: a write failure is not a reason to hide what the instance reports.
    expect(screen.getByTestId("configuration-panel")).toBeInTheDocument();
    expect(screen.queryByTestId("config-unavailable")).toBeNull();
    expect(screen.getByTestId(PLUGIN_ROW)).toBeInTheDocument();
  });

  it("discloses the pending-restart set with the running and the pending value", async () => {
    getConfigMock.mockResolvedValue(
      config([setting({ value: "128", source: "override", restartPending: true })], {
        pendingRestart: [
          { key: "Fallen8:Plugins:MaxCount", runningValue: "64", pendingValue: "128" },
        ],
      }),
    );
    renderPanel();

    const banner = await screen.findByTestId("config-pending-restart");
    expect(banner).toHaveTextContent(/differs from what this instance started with/);
    expect(banner).toHaveTextContent("64");
    expect(banner).toHaveTextContent("128");
  });

  it("offers a Clear only for a row whose stored value is the one in force", async () => {
    const user = userEvent.setup();
    getConfigMock.mockResolvedValue(config([setting({ value: "128", source: "override" })]));
    writeConfigMock.mockResolvedValue({ results: [], pendingRestart: [] });
    renderPanel();

    await user.click(await screen.findByTestId(`${PLUGIN_ROW}-clear`));
    await user.click(screen.getByTestId("config-save"));

    // null is the undo: it removes the stored value rather than writing a new one.
    await waitFor(() => expect(writeConfigMock).toHaveBeenCalledTimes(1));
    expect(writeConfigMock.mock.calls[0][1]).toEqual({
      settings: { "Fallen8:Plugins:MaxCount": null },
    });
  });

  it("is read-only when the instance has no API key, because a write would always be refused", async () => {
    getConfigMock.mockResolvedValue(config([setting()], { apiKeyRequired: false }));
    renderPanel();

    expect(await screen.findByTestId(PLUGIN_ROW)).toBeDisabled();
    expect(screen.getByTestId("configuration-panel")).toHaveTextContent(
      /configuring an API key is what allows a write/,
    );
  });

  it("gates the editable region on lockInstances, and the namespace policy on lockNamespace too", async () => {
    getConfigMock.mockResolvedValue(
      config([setting(), setting({ key: "Fallen8:Namespaces:LoadOnStartup", kind: "bool", value: "true" })]),
    );

    const locked = renderPanel({ lockInstances: true });
    expect(await screen.findByTestId(PLUGIN_ROW)).toBeDisabled();
    locked.unmount();

    // An embed that locked namespace management must not be able to re-plan the host's next boot
    // through the instance-wide startup default either, even though instances are otherwise editable.
    renderPanel({ lockNamespace: true });
    const startup = settingTestId("Fallen8:Namespaces:LoadOnStartup");
    await waitFor(() => expect(screen.getByTestId(startup)).toBeDisabled());
    expect(screen.getByTestId(PLUGIN_ROW)).not.toBeDisabled();
  });

  it("keeps working against an instance that publishes no settings at all", async () => {
    // An older server answers without the new fields; the panel must read them defensively rather
    // than crashing the Connect screen.
    getConfigMock.mockResolvedValue(config([], { settings: undefined, pendingRestart: undefined }));
    renderPanel();

    expect(await screen.findByTestId("configuration-panel")).toBeInTheDocument();
    expect(screen.queryByTestId("config-pending-restart")).toBeNull();
    expect(screen.queryByTestId("config-save")).toBeNull();
  });
});
