// MIT License
//
// integrations-screen.test.tsx
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
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { ApiError } from "../src/api/client";
import type { InstanceConfig } from "../src/instances/types";
import type {
  IntegrationJobFile,
  IntegrationJobReport,
  IntegrationJobRequest,
  IntegrationProvider,
  IntegrationRunAccepted,
  IntegrationRunState,
  IntegrationSetting,
  StatusREST,
} from "../src/api/types";
import { getInstanceStore, resetInstanceStoresForTests } from "../src/state/instanceStore";
import { SAME_ORIGIN_INSTANCE } from "../src/instances/registry";

const store = () => getInstanceStore(SAME_ORIGIN_INSTANCE.id);
import { capList, LIST_MAX_ROWS, SCROLL_ROWS } from "../src/lib/listCaps";
import { ListCapNote } from "../src/components/ListCapNote";

/**
 * Integrations screen (feature integrations). The load-bearing test is the first one: the form is
 * rendered from a DESCRIPTOR THE SCREEN HAS NEVER SEEN, which is what makes "adding a fourth
 * integration needs no Studio change" a fact rather than a claim. The rest pin the credential
 * field (the secret itself, which the form forgets once the job reports), the job split that keeps it
 * out of `settings`, the identity shape check that avoids a 400 rather than explaining it, the report,
 * and the two ways an instance says it has no runtime.
 */

const listProvidersMock = vi.fn<(i: InstanceConfig) => Promise<IntegrationProvider[] | null>>();
// The POST answers a run ID now, not a report: the report is read afterwards from the run, because a
// real import outlives the connection that would have carried it.
/** What the screen passes for progress and cancellation (feature integration-file-transport). */
type SendOptions = {
  signal?: AbortSignal;
  onProgress?: (progress: { sent: number; total: number | null }) => void;
};
const submitJobMock =
  vi.fn<
    (
      i: InstanceConfig,
      job: IntegrationJobRequest,
      options?: SendOptions,
    ) => Promise<IntegrationRunAccepted | null>
  >();
const getRunMock =
  vi.fn<(i: InstanceConfig, instanceId: string) => Promise<IntegrationRunState | null>>();
// The cancel answers the run as it was when the stop was RECORDED, so the panel can show the stop as
// pending without waiting out a poll interval.
const cancelRunMock =
  vi.fn<(i: InstanceConfig, instanceId: string) => Promise<IntegrationRunState | null>>();
const getStatusMock =
  vi.fn<(i: InstanceConfig, signal?: AbortSignal) => Promise<StatusREST | null>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    listIntegrationProviders: (i: InstanceConfig) => listProvidersMock(i),
    submitIntegrationJob: (i: InstanceConfig, job: IntegrationJobRequest, options?: SendOptions) =>
      submitJobMock(i, job, options),
    getIntegrationRun: (i: InstanceConfig, instanceId: string) => getRunMock(i, instanceId),
    cancelIntegrationRun: (i: InstanceConfig, instanceId: string) => cancelRunMock(i, instanceId),
    getStatus: (i: InstanceConfig, s?: AbortSignal) => getStatusMock(i, s),
  };
});

import { IntegrationsScreen } from "../src/screens/IntegrationsScreen";

/**
 * A provider this screen has never heard of, with one setting of every kind. Nothing about it is
 * special-cased anywhere, which is the whole point.
 */
function fourthIntegration(): IntegrationProvider {
  return {
    id: "hypothetical-fourth",
    displayName: "Hypothetical fourth",
    description: "Reads a system nobody has written a screen for.",
    settings: [
      { key: "baseUrl", label: "Base URL", kind: "Url", required: true, help: "Where the thing lives." },
      { key: "page", label: "Page size", kind: "Number", required: false, help: "How many at a time.", defaultValue: "50" },
      { key: "verbose", label: "Verbose", kind: "Boolean", required: false, help: "Say more." },
      { key: "label", label: "Label", kind: "Text", required: false, help: "What to call the rows." },
      { key: "apiKey", label: "API key", kind: "Credential", required: true, help: "Created under Settings then Integrations." },
      { key: "extract", label: "Extract", kind: "File", required: false, help: "The file itself, sent with the job.", accept: ".csv,.tsv" },
    ],
    entityKinds: ["thing"],
    claimTypes: ["mac"],
    relationTypes: [],
    canObserveCompleteState: true,
    readOnly: true,
  };
}

/**
 * The same integration with its file setting declared `multiple`, which is a statement about the
 * SOURCE: a vehicle network is handed over as one AUTOSAR extract per domain or per bus, and those
 * extracts reference each other, so no single file is a complete description of it.
 */
function multiFileIntegration(overrides: Partial<IntegrationSetting> = {}): IntegrationProvider {
  const provider = fourthIntegration();
  provider.settings = provider.settings.map((setting) =>
    setting.key === "extract"
      ? { ...setting, multiple: true, accept: ".arxml", ...overrides }
      : setting,
  );
  return provider;
}

/**
 * The one file a setting that takes ONE was given, asserting the SHAPE on the way: the runtime
 * refuses a list of one there, so a job that sent an array would be refused with nothing run.
 */
function oneFile(job: IntegrationJobRequest, key: string): IntegrationJobFile {
  const sent = job.files?.[key];
  expect(Array.isArray(sent)).toBe(false);
  return sent as IntegrationJobFile;
}

/** The ordered list a `multiple` setting was given, asserting that it IS a list. */
function fileList(job: IntegrationJobRequest, key: string): IntegrationJobFile[] {
  const sent = job.files?.[key];
  expect(Array.isArray(sent)).toBe(true);
  return sent as IntegrationJobFile[];
}

function report(overrides: Partial<IntegrationJobReport> = {}): IntegrationJobReport {
  return {
    providerId: "hypothetical-fourth",
    integrationInstanceId: "office-inventory",
    startedUtc: "2026-08-11T09:12:44.1180000+00:00",
    durationMilliseconds: 148,
    elementsCreated: 3,
    elementsMatched: 39,
    edgesCreated: 2,
    claimsWithdrawn: 1,
    elementsDeleted: 1,
    deletionsDeferred: 0,
    issuedMutations: true,
    summariesEmbedded: 0,
    error: null,
    errorKind: null,
    credentialFingerprint: "a1b2c3d4e5f6",
    diagnostics: [
      { code: "rowWithoutMac", message: "no MAC in this row", subject: "devices.csv row 7" },
    ],
    ...overrides,
  };
}

/**
 * A status whose embedding block says the provider is on or off. The embed opt-in gates on it, because
 * a run that asks a target with no provider to embed succeeds and embeds nothing.
 */
function status(embeddingEnabled: boolean): StatusREST {
  return {
    vertexCount: 0,
    edgeCount: 0,
    usedMemory: 0,
    indices: [],
    availableIndexPlugins: [],
    availablePathPlugins: [],
    availableAnalyticsPlugins: [],
    availableServicePlugins: [],
    embedding: {
      enabled: embeddingEnabled,
      backend: "Ollama",
      modelName: "bge-m3",
      modelVersion: null,
      dimension: 1024,
      intendedMetric: "Cosine",
      loaded: false,
    },
  };
}

function accepted(): IntegrationRunAccepted {
  return {
    runId: "run-abc",
    providerId: "hypothetical-fourth",
    integrationInstanceId: "office-inventory",
    progress: "/integration/run/office-inventory",
  };
}

/** A run that has ended, carrying its report - which is where the report now comes from. */
function finishedRun(withReport: IntegrationJobReport, embedRequested = false): IntegrationRunState {
  return {
    runId: "run-abc",
    providerId: "hypothetical-fourth",
    integrationInstanceId: "office-inventory",
    startedAt: "2026-08-25T09:00:00.0000000Z",
    finishedAt: "2026-08-25T09:00:04.0000000Z",
    running: false,
    elapsedMilliseconds: 4000,
    phase: null,
    phaseDone: 0,
    phaseTotal: 0,
    completedPhases: ["observe", "validate", "resolve", "write-elements"],
    embedRequested,
    report: withReport,
  };
}

/** A run still in flight, in the phase that used to look exactly like a hang. */
function runningRun(phase: string, done: number, total: number): IntegrationRunState {
  return {
    ...finishedRun(report()),
    finishedAt: null,
    running: true,
    elapsedMilliseconds: 3 * 3600 * 1000 + 7 * 60 * 1000,
    phase,
    phaseDone: done,
    phaseTotal: total,
    completedPhases: ["observe", "validate", "resolve", "write-elements", "write-edges"],
    report: null,
  };
}

/** A run a stop has been ASKED for, still going because it has not reached a safe point yet. */
function stoppingRun(): IntegrationRunState {
  return { ...runningRun("embed-summaries", 4320, 9478), cancelRequested: true };
}

/**
 * A run that ENDED because it was cancelled: a third terminal state beside succeeded and failed. It
 * carries counts and no errorKind, which is exactly why it needs its own rendering - without one it
 * is indistinguishable from a clean import.
 */
function cancelledRun(): IntegrationRunState {
  return {
    ...finishedRun(
      report({
        cancelled: true,
        elementsCreated: 6,
        elementsMatched: 11,
        claimsWithdrawn: 0,
        elementsDeleted: 0,
        error: null,
        errorKind: null,
        diagnostics: [],
      }),
    ),
    cancelRequested: true,
    cancelled: true,
    stoppedInPhase: "embed-summaries",
  };
}

/**
 * A run that was PICKED UP after the runtime restarted. Deliberately built from the in-flight run
 * unchanged apart from the flag: the pickup keeps the run id, the original start time and the phases
 * the first attempt got through, so anything the panel needs to special-case has to come from
 * `resumed` alone.
 */
function resumedRun(overrides: Partial<IntegrationRunState> = {}): IntegrationRunState {
  return { ...runningRun("write-edges", 8, 31), resumed: true, ...overrides };
}

function renderScreen() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <IntegrationsScreen />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  resetInstanceStoresForTests();
  localStorage.clear();
  listProvidersMock.mockReset().mockResolvedValue([fourthIntegration()]);
  submitJobMock.mockReset().mockResolvedValue(accepted());
  getRunMock.mockReset().mockResolvedValue(finishedRun(report()));
  cancelRunMock.mockReset().mockResolvedValue(stoppingRun());
  getStatusMock.mockReset().mockResolvedValue(status(true));
});

describe("the settings form is rendered from the descriptor alone", () => {
  it("renders one input per setting, of the kind the descriptor declares, with the descriptor's own help", async () => {
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    // A screen that special-cased providers would have nothing to show for a provider it has never
    // seen. Each input's TYPE comes from the kind, and each help text from the descriptor.
    expect(screen.getByTestId("integration-setting-baseUrl")).toHaveAttribute("type", "url");
    expect(screen.getByTestId("integration-setting-page")).toHaveAttribute("type", "number");
    expect(screen.getByTestId("integration-setting-verbose")).toHaveAttribute("type", "checkbox");
    expect(screen.getByTestId("integration-setting-label")).toHaveAttribute("type", "text");
    expect(screen.getByText("Where the thing lives.")).toBeInTheDocument();
    expect(screen.getByText("How many at a time.")).toBeInTheDocument();
  });

  it("opens a setting on the descriptor's own default rather than on a blank", async () => {
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    expect(screen.getByTestId("integration-setting-page")).toHaveValue(50);
  });

  it("falls back to a text input for a kind this Studio does not know", async () => {
    const future = fourthIntegration();
    // An integration built against a newer runtime, running against this Studio.
    future.settings = [
      { key: "exotic", label: "Exotic", kind: "Duration" as never, required: false, help: "From the future." },
    ];
    listProvidersMock.mockResolvedValue([future]);

    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    expect(screen.getByTestId("integration-setting-exotic")).toHaveAttribute("type", "text");
  });
});

describe("a file setting takes the file itself", () => {
  it("renders a dropzone and a picker instead of a box to type a file name into", async () => {
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    // The whole point of the kind. Before it, a file-taking integration asked for the NAME of a file
    // the operator first had to copy into a directory mounted into the runtime's container - which in
    // the shipped environment held no such file at all.
    expect(screen.getByTestId("integration-setting-extract-dropzone")).toBeInTheDocument();
    const picker = screen.getByTestId("integration-setting-extract");
    expect(picker).toHaveAttribute("type", "file");
    expect(picker).toHaveAttribute("accept", ".csv,.tsv");
  });

  it("will not submit until a file is staged, and names the field that is missing", async () => {
    const needsFile = fourthIntegration();
    needsFile.settings = needsFile.settings.map((s) =>
      s.key === "extract" ? { ...s, required: true } : s,
    );
    listProvidersMock.mockResolvedValue([needsFile]);

    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    await userEvent.type(screen.getByTestId("integration-instance-id"), "office-inventory");
    await userEvent.type(screen.getByTestId("integration-setting-baseUrl"), "https://thing.invalid");
    await userEvent.type(screen.getByTestId("integration-setting-apiKey"), "secret");

    // A required file cannot be satisfied by a typed value - the runtime refuses a file setting named
    // in `settings` - so the only thing that satisfies it is a staged file.
    expect(screen.getByTestId("integration-run")).toBeDisabled();
    expect(screen.getByTestId("integration-missing")).toHaveTextContent("Extract");
  });

  it("sends the file HANDLE in files, and its name never in settings", async () => {
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    await userEvent.type(screen.getByTestId("integration-instance-id"), "office-inventory");
    await userEvent.type(screen.getByTestId("integration-setting-baseUrl"), "https://thing.invalid");
    await userEvent.type(screen.getByTestId("integration-setting-apiKey"), "secret");

    const picked = new File(["mac,name\nAA:BB:CC:DD:EE:01,Reception\n"], "devices.csv", {
      type: "text/csv",
    });
    await userEvent.upload(screen.getByTestId("integration-setting-extract"), picked);

    // Staging, not sending: a run also needs an identity and the other settings, so the file waits.
    await waitFor(() =>
      expect(screen.getByTestId("integration-setting-extract-staged")).toHaveTextContent("devices.csv"),
    );
    expect(submitJobMock).not.toHaveBeenCalled();

    await userEvent.click(screen.getByTestId("integration-run"));

    await waitFor(() => expect(submitJobMock).toHaveBeenCalledTimes(1));
    const job = submitJobMock.mock.calls[0][1];
    const sent = oneFile(job, "extract");
    expect(sent.name).toBe("devices.csv");
    // IDENTICALLY the object that was picked. Not a copy, not bytes, not a re-encode - and asserted
    // by identity on purpose: a test comparing CONTENT would pass just as well against the version
    // this replaced, which read every file into memory at pick time and base64'd it at send time.
    // Reference equality is the only assertion that fails if anyone reintroduces that.
    expect(sent.file).toBe(picked);
    // The name in `settings` is what the runtime refuses, and putting it there would be the one
    // mistake that looks like it works right up to the 400.
    expect(job.settings).not.toHaveProperty("extract");
  });

  it("refuses an empty file in the form rather than spending a round trip on it", async () => {
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    await userEvent.upload(
      screen.getByTestId("integration-setting-extract"),
      new File([], "empty.csv", { type: "text/csv" }),
    );

    await waitFor(() =>
      expect(screen.getByTestId("integration-setting-extract-problem")).toHaveTextContent("empty"),
    );
    // And it is NOT staged: an empty file read as an empty source is a complete snapshot describing
    // nothing, which withdraws every element the identity ever claimed.
    expect(screen.queryByTestId("integration-setting-extract-staged")).toBeNull();
  });

  it("keeps the drop target after staging, and removing clears the file", async () => {
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    await userEvent.upload(
      screen.getByTestId("integration-setting-extract"),
      new File(["mac\n"], "devices.csv", { type: "text/csv" }),
    );
    await waitFor(() =>
      expect(screen.getByTestId("integration-setting-extract-staged")).toBeInTheDocument(),
    );

    // The zone has to survive staging. Swapping it for a plain row would leave the form with no drop
    // target, so a second drop would land on the document and navigate away from a half-filled form.
    expect(screen.getByTestId("integration-setting-extract-dropzone")).toBeInTheDocument();

    await userEvent.click(screen.getByTestId("integration-setting-extract-clear"));
    expect(screen.queryByTestId("integration-setting-extract-staged")).toBeNull();
  });

  it("drops a staged file when another integration is selected", async () => {
    const other = fourthIntegration();
    other.id = "another-fourth";
    other.displayName = "Another fourth";
    listProvidersMock.mockResolvedValue([fourthIntegration(), other]);

    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));
    await userEvent.upload(
      screen.getByTestId("integration-setting-extract"),
      new File(["mac\n"], "devices.csv", { type: "text/csv" }),
    );
    await waitFor(() =>
      expect(screen.getByTestId("integration-setting-extract-staged")).toBeInTheDocument(),
    );

    await userEvent.click(screen.getByTestId("integration-select-another-fourth"));

    // Two integrations can declare the same setting key, so a file that rode along would send one
    // integration's extract to another - and nothing afterwards could tell that had happened.
    expect(screen.queryByTestId("integration-setting-extract-staged")).toBeNull();
  });
});

describe("a file setting can take a SET of files (feature integration-run-lifecycle)", () => {
  /** Selects the given provider and fills everything a run needs except the files. */
  async function selectAndFill(provider: IntegrationProvider) {
    listProvidersMock.mockResolvedValue([provider]);
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));
    await userEvent.type(screen.getByTestId("integration-instance-id"), "vehicle-network");
    await userEvent.type(screen.getByTestId("integration-setting-baseUrl"), "https://thing.invalid");
    await userEvent.type(screen.getByTestId("integration-setting-apiKey"), "secret");
  }

  function extract(name: string, body = `<AUTOSAR>${name}</AUTOSAR>`): File {
    return new File([body], name, { type: "application/xml" });
  }

  const rows = () => screen.getAllByTestId("integration-setting-extract-staged-file");

  it("renders a multi-capable picker for a setting the descriptor declares multiple", async () => {
    await selectAndFill(multiFileIntegration());

    // Without the attribute the picker takes one file per visit to the dialog, which for a handover
    // of eight domain extracts is eight visits and eight chances to miss one.
    const picker = screen.getByTestId("integration-setting-extract");
    expect(picker).toHaveAttribute("multiple");
    // The accept hint is still the descriptor's, since taking several files says nothing about what
    // kind they are.
    expect(picker).toHaveAttribute("accept", ".arxml");
  });

  it("leaves a single-file setting exactly as it was, with no multi-capable control", async () => {
    await selectAndFill(fourthIntegration());

    expect(screen.getByTestId("integration-setting-extract")).not.toHaveAttribute("multiple");
    expect(screen.queryByTestId("integration-setting-extract-staged-list")).toBeNull();
  });

  it("stages several files and sends them as ONE ordered array", async () => {
    await selectAndFill(multiFileIntegration());

    const picked = [extract("body.arxml"), extract("chassis.arxml"), extract("powertrain.arxml")];
    await userEvent.upload(screen.getByTestId("integration-setting-extract"), picked);
    await waitFor(() => expect(rows()).toHaveLength(3));

    await userEvent.click(screen.getByTestId("integration-run"));
    await waitFor(() => expect(submitJobMock).toHaveBeenCalledTimes(1));

    // ORDER IS MEANING, not presentation: the reader resolves references across the union of the
    // files and gives a re-declared path to the one listed first, so the order the operator sees
    // has to be the order the job carries.
    const sent = fileList(submitJobMock.mock.calls[0][1], "extract");
    expect(sent.map((file) => file.name)).toEqual([
      "body.arxml",
      "chassis.arxml",
      "powertrain.arxml",
    ]);
    // The same handles, in that order: identity again, so a copy or a re-encode fails here.
    expect(sent.map((file) => file.file)).toEqual(picked);
  });

  it("ADDS a second drop to the set rather than replacing it", async () => {
    await selectAndFill(multiFileIntegration());

    await userEvent.upload(screen.getByTestId("integration-setting-extract"), [
      extract("body.arxml"),
    ]);
    await waitFor(() => expect(rows()).toHaveLength(1));

    fireEvent.drop(screen.getByTestId("integration-setting-extract-dropzone"), {
      dataTransfer: { files: [extract("chassis.arxml")] },
    });

    // Replacing would silently discard the extract already picked, which is the one failure a
    // domain-at-a-time handover cannot notice: the run would then declare a complete source that
    // never mentioned the body bus, and reconciliation would delete everything it had described.
    await waitFor(() => expect(rows()).toHaveLength(2));
    await userEvent.click(screen.getByTestId("integration-run"));
    await waitFor(() => expect(submitJobMock).toHaveBeenCalledTimes(1));
    expect(fileList(submitJobMock.mock.calls[0][1], "extract").map((file) => file.name)).toEqual([
      "body.arxml",
      "chassis.arxml",
    ]);
  });

  it("takes every file of ONE multi-file drop, not just the first", async () => {
    await selectAndFill(multiFileIntegration());

    fireEvent.drop(screen.getByTestId("integration-setting-extract-dropzone"), {
      dataTransfer: { files: [extract("body.arxml"), extract("chassis.arxml")] },
    });

    // Dragging the whole handover onto the target at once is the point of the field; taking [0]
    // ignored the rest without a word about it.
    await waitFor(() => expect(rows()).toHaveLength(2));
  });

  it("removes one file at a time, and clears the field when the last one goes", async () => {
    await selectAndFill(multiFileIntegration());

    await userEvent.upload(screen.getByTestId("integration-setting-extract"), [
      extract("body.arxml"),
      extract("chassis.arxml"),
    ]);
    await waitFor(() => expect(rows()).toHaveLength(2));

    // The wrong file in the set is the common correction, and re-picking the other seven to fix one
    // is what a per-file remove exists to avoid.
    await userEvent.click(screen.getByTestId("integration-setting-extract-remove-0"));
    await waitFor(() => expect(rows()).toHaveLength(1));
    expect(rows()[0]).toHaveTextContent("chassis.arxml");

    await userEvent.click(screen.getByTestId("integration-setting-extract-remove-0"));
    expect(screen.queryByTestId("integration-setting-extract-staged")).toBeNull();
    expect(screen.queryByTestId("integration-setting-extract-staged-list")).toBeNull();
  });

  it("refuses a duplicate name in the form, with case set aside as the runtime sets it aside", async () => {
    await selectAndFill(multiFileIntegration());

    await userEvent.upload(screen.getByTestId("integration-setting-extract"), [
      extract("body.arxml"),
    ]);
    await waitFor(() => expect(rows()).toHaveLength(1));

    await userEvent.upload(screen.getByTestId("integration-setting-extract"), [
      extract("Body.arxml"),
    ]);

    // The runtime refuses the WHOLE job for this, because every diagnostic about a file names it and
    // two files with one name make each of those messages ambiguous. Caught here, the fix costs one
    // pick rather than a rejected run.
    await waitFor(() =>
      expect(screen.getByTestId("integration-setting-extract-problem")).toHaveTextContent(
        /already staged/,
      ),
    );
    expect(rows()).toHaveLength(1);
  });

  it("shows the total of the set, because the total is a refusal of its own", async () => {
    await selectAndFill(multiFileIntegration());

    await userEvent.upload(screen.getByTestId("integration-setting-extract"), [
      extract("body.arxml", "a".repeat(1024)),
      extract("chassis.arxml", "b".repeat(1024)),
    ]);

    // Per-file size answers the per-file ceiling; only the total answers the per-JOB one, and a set
    // of extracts is exactly where the second bites first.
    await waitFor(() =>
      expect(screen.getByTestId("integration-setting-extract-total")).toHaveTextContent(
        "2 files, 2.0 KiB in total",
      ),
    );
    expect(rows()[0]).toHaveTextContent("1.0 KiB");
  });

  it("will not submit until a required set has at least one file in it", async () => {
    await selectAndFill(multiFileIntegration({ required: true }));

    expect(screen.getByTestId("integration-run")).toBeDisabled();
    expect(screen.getByTestId("integration-missing")).toHaveTextContent("Extract");

    await userEvent.upload(screen.getByTestId("integration-setting-extract"), [
      extract("body.arxml"),
    ]);

    // One file is enough for the FORM: whether the set is complete is a judgement only the operator
    // can make, and the runtime never guesses at it either.
    await waitFor(() => expect(screen.getByTestId("integration-run")).toBeEnabled());
  });

  it("says the set of files is the source, which is the sharp edge of running with fewer", async () => {
    await selectAndFill(multiFileIntegration());

    // The failure this copy exists to prevent: each snapshot honestly declares itself complete, so a
    // re-run missing one extract withdraws and deletes everything only that extract described.
    expect(screen.getByText(/set of files is the source/)).toBeInTheDocument();
    expect(screen.getByText(/withdraws whatever only the missing file described/)).toBeInTheDocument();
  });

  it("still sends the bare object for a single-file setting, which is all the runtime accepts there", async () => {
    await selectAndFill(fourthIntegration());

    await userEvent.upload(
      screen.getByTestId("integration-setting-extract"),
      extract("devices.csv", "mac\n"),
    );
    await waitFor(() =>
      expect(screen.getByTestId("integration-setting-extract-staged")).toHaveTextContent(
        "devices.csv",
      ),
    );

    await userEvent.click(screen.getByTestId("integration-run"));
    await waitFor(() => expect(submitJobMock).toHaveBeenCalledTimes(1));

    // A list of one here is refused by the runtime with a message about a setting that takes ONE
    // file, so the shape has to follow the descriptor and not the count.
    expect(oneFile(submitJobMock.mock.calls[0][1], "extract").name).toBe("devices.csv");
  });

  it("still replaces rather than appends for a single-file setting", async () => {
    await selectAndFill(fourthIntegration());

    await userEvent.upload(
      screen.getByTestId("integration-setting-extract"),
      extract("devices.csv", "mac\n"),
    );
    await waitFor(() =>
      expect(screen.getByTestId("integration-setting-extract-staged")).toHaveTextContent(
        "devices.csv",
      ),
    );

    await userEvent.upload(
      screen.getByTestId("integration-setting-extract"),
      extract("printers.csv", "mac\n"),
    );

    await waitFor(() =>
      expect(screen.getByTestId("integration-setting-extract-staged")).toHaveTextContent(
        "printers.csv",
      ),
    );
    expect(screen.getByTestId("integration-setting-extract-staged")).not.toHaveTextContent(
      "devices.csv",
    );
  });
});

describe("a credential setting takes the secret, and the form forgets it", () => {
  it("renders as a password box saying the value is used and then forgotten", async () => {
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    // A password box, because the field takes the secret itself: there is no credential store to name
    // one in, and a text box would put it in the clear on somebody's screen.
    expect(screen.getByTestId("integration-setting-apiKey")).toHaveAttribute("type", "password");
    expect(screen.getByText(/used for this run and then forgotten/)).toBeInTheDocument();
  });

  it("sends the secret in credentialValues and never in settings", async () => {
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    await userEvent.type(screen.getByTestId("integration-instance-id"), "office-inventory");
    await userEvent.type(screen.getByTestId("integration-setting-baseUrl"), "https://thing.invalid");
    await userEvent.type(screen.getByTestId("integration-setting-apiKey"), "the-real-secret");
    await userEvent.click(screen.getByTestId("integration-run"));

    await waitFor(() => expect(submitJobMock).toHaveBeenCalledTimes(1));
    const job = submitJobMock.mock.calls[0][1];
    // A setting is neither leased nor redacted by the runtime, so a secret there would be logged and
    // reported like any other value.
    expect(job.credentialValues).toEqual({ apiKey: "the-real-secret" });
    expect(job.settings).not.toHaveProperty("apiKey");
    expect(job.settings.baseUrl).toBe("https://thing.invalid");
  });

  it("sends a secret VERBATIM, because a space can be part of a real password", async () => {
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    await userEvent.type(screen.getByTestId("integration-instance-id"), "office-inventory");
    await userEvent.type(screen.getByTestId("integration-setting-baseUrl"), "https://thing.invalid");
    await userEvent.type(screen.getByTestId("integration-setting-apiKey"), " pa ss ");
    await userEvent.click(screen.getByTestId("integration-run"));

    await waitFor(() => expect(submitJobMock).toHaveBeenCalledTimes(1));
    // Trimming here would produce an authentication failure from somebody's controller with nothing
    // on the report to explain it. The runtime drops exactly one trailing newline and nothing else.
    expect(submitJobMock.mock.calls[0][1].credentialValues).toEqual({ apiKey: " pa ss " });
  });

  it("clears the secret once the job reports, and keeps the ordinary settings", async () => {
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    await userEvent.type(screen.getByTestId("integration-instance-id"), "office-inventory");
    await userEvent.type(screen.getByTestId("integration-setting-baseUrl"), "https://thing.invalid");
    await userEvent.type(screen.getByTestId("integration-setting-apiKey"), "the-real-secret");
    await userEvent.click(screen.getByTestId("integration-run"));

    await screen.findByTestId("report-created");
    // The whole promise of supplying a value inline: it is used and then gone. A form still holding it
    // is a secret sitting in a tab somebody walks away from.
    expect(screen.getByTestId("integration-setting-apiKey")).toHaveValue("");
    // The other settings are not a secret and re-typing them after each run would be a nuisance.
    expect(screen.getByTestId("integration-setting-baseUrl")).toHaveValue("https://thing.invalid");
  });

});

describe("the identity is checked before anything is sent", () => {
  it("refuses a malformed integrationInstanceId in the form rather than explaining the 400", async () => {
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    await userEvent.type(screen.getByTestId("integration-instance-id"), "garage:one");
    await userEvent.type(screen.getByTestId("integration-setting-baseUrl"), "https://thing.invalid");
    await userEvent.type(screen.getByTestId("integration-setting-apiKey"), "thing-key");

    expect(screen.getByTestId("integration-instance-id-problem")).toBeInTheDocument();
    expect(screen.getByTestId("integration-run")).toBeDisabled();
    // A colon would let two identities compose one identical claim key, so nothing may be sent.
    expect(submitJobMock).not.toHaveBeenCalled();
  });

  it("will not submit while a required setting is empty, and names what is missing", async () => {
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));
    await userEvent.type(screen.getByTestId("integration-instance-id"), "office-inventory");

    expect(screen.getByTestId("integration-missing")).toHaveTextContent("Base URL");
    expect(screen.getByTestId("integration-run")).toBeDisabled();
  });
});

describe("the report is shown as the runtime wrote it", () => {
  it("renders the counts, whether anything was written, and every diagnostic code", async () => {
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));
    await userEvent.type(screen.getByTestId("integration-instance-id"), "office-inventory");
    await userEvent.type(screen.getByTestId("integration-setting-baseUrl"), "https://thing.invalid");
    await userEvent.type(screen.getByTestId("integration-setting-apiKey"), "thing-key");
    await userEvent.click(screen.getByTestId("integration-run"));

    expect(await screen.findByTestId("report-created")).toHaveTextContent("3");
    expect(screen.getByTestId("report-matched")).toHaveTextContent("39");
    expect(screen.getByTestId("report-withdrawn")).toHaveTextContent("1");
    expect(screen.getByTestId("report-deleted")).toHaveTextContent("1");
    expect(screen.getByTestId("report-mutations")).toHaveTextContent("yes");
    // The CODE is the contract a reader greps for and alerts on, so it is shown rather than
    // translated into this screen's own words.
    expect(screen.getByTestId("report-diagnostic-code")).toHaveTextContent("rowWithoutMac");
  });

  it("shows a failed run's kind and says nothing was withdrawn", async () => {
    getRunMock.mockResolvedValue(
      finishedRun(
        report({
          errorKind: "source",
          error: "the console did not answer",
          elementsCreated: 0,
          elementsMatched: 0,
          claimsWithdrawn: 0,
          elementsDeleted: 0,
          issuedMutations: false,
          diagnostics: [],
        }),
      ),
    );

    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));
    await userEvent.type(screen.getByTestId("integration-instance-id"), "office-inventory");
    await userEvent.type(screen.getByTestId("integration-setting-baseUrl"), "https://thing.invalid");
    await userEvent.type(screen.getByTestId("integration-setting-apiKey"), "thing-key");
    await userEvent.click(screen.getByTestId("integration-run"));

    const failure = await screen.findByTestId("integration-report-error");
    expect(failure).toHaveTextContent("source");
    expect(failure).toHaveTextContent(/[Nn]othing was withdrawn/);
  });
});

describe("an instance with no runtime says so", () => {
  it("renders the absent panel on a 403, which is what a secured instance answers", async () => {
    listProvidersMock.mockRejectedValue(
      new ApiError(403, "/integrations/providers", "capability off"),
    );

    renderScreen();

    expect(await screen.findByTestId("integrations-absent")).toBeInTheDocument();
    // An error box would read as "something broke"; there is simply nothing there.
    expect(screen.queryByText(/HTTP 403/)).not.toBeInTheDocument();
  });

  it("renders the absent panel on a 401 too, which is what an OPEN instance answers", async () => {
    // With no API key configured the standing capability policy challenges before it forbids, so
    // keying on 403 alone would show a broken screen on exactly the default local setup.
    listProvidersMock.mockRejectedValue(
      new ApiError(401, "/integrations/providers", "unauthorized"),
    );

    renderScreen();

    expect(await screen.findByTestId("integrations-absent")).toBeInTheDocument();
  });

  it("shows a real error for anything else, because an unreachable instance is not an absent capability", async () => {
    listProvidersMock.mockRejectedValue(
      new ApiError(500, "/integrations/providers", "boom"),
    );

    renderScreen();

    expect(await screen.findByText(/HTTP 500/)).toBeInTheDocument();
    expect(screen.queryByTestId("integrations-absent")).not.toBeInTheDocument();
  });
});

describe("the provider list obeys the standing list-cap policy", () => {
  it("caps the HEIGHT of both lists rather than hiding rows, at this screen's own thresholds", async () => {
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    // The policy is a height cap with a scrollbar, never dropped rows, and a screen opts in with the
    // wrapper plus the row threshold. Rendering the 10,000-row ceiling here would cost half a minute
    // in jsdom for a rule that belongs to the shared helper, so the ceiling is pinned below instead.
    const wrappers = document.querySelectorAll(".scroll-list");
    expect(wrappers.length).toBeGreaterThanOrEqual(1);
    expect((wrappers[0] as HTMLElement).style.getPropertyValue("--scroll-rows")).toBe(
      String(SCROLL_ROWS.integrations),
    );
  });

  it("truncates only at the hard ceiling, and reports the true total so the note can disclose it", () => {
    const many = Array.from({ length: LIST_MAX_ROWS + 50 }, (_, index) => index);

    const capped = capList(many);

    expect(capped.shown.length).toBe(LIST_MAX_ROWS);
    expect(capped.total).toBe(LIST_MAX_ROWS + 50);
  });

  it("says nothing when nothing was dropped, and discloses it when something was", () => {
    const { container, rerender } = render(<ListCapNote shown={2} total={2} />);
    expect(container).toBeEmptyDOMElement();

    rerender(<ListCapNote shown={2} total={5} />);
    expect(screen.getByText(/Showing the first/)).toBeInTheDocument();
  });
});

describe("the embed opt-in, which is the only way a Studio run writes summary embeddings", () => {
  it("is not offered at all for a provider that declares no summary template", async () => {
    listProvidersMock.mockResolvedValue([{ ...fourthIntegration(), entitySummaryTemplate: null }]);
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    // Offering it would be offering a control that cannot work: with no template there is no text to
    // embed, so the run would succeed and embed nothing.
    expect(screen.queryByTestId("integration-embed")).not.toBeInTheDocument();
  });

  it("sends BOTH halves of the opt-in, because the runtime needs both", async () => {
    listProvidersMock.mockResolvedValue([{ ...fourthIntegration(), entitySummaryTemplate: "{kind} {csv.name}" }]);
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    await userEvent.type(screen.getByTestId("integration-instance-id"), "office-inventory");
    await userEvent.type(screen.getByTestId("integration-setting-baseUrl"), "https://thing.invalid");
    await userEvent.type(screen.getByTestId("integration-setting-apiKey"), "secret");
    await userEvent.click(await screen.findByTestId("integration-embed-toggle"));
    await userEvent.type(await screen.findByTestId("integration-embed-name"), "arxml-summary");
    await userEvent.click(screen.getByTestId("integration-run"));

    await waitFor(() => expect(submitJobMock).toHaveBeenCalledTimes(1));
    const job = submitJobMock.mock.calls[0][1];
    expect(job.embedSummaries).toBe(true);
    expect(job.embeddingName).toBe("arxml-summary");
  });

  it("sends NEITHER half when the operator did not ask, because embedding is opt-in", async () => {
    listProvidersMock.mockResolvedValue([{ ...fourthIntegration(), entitySummaryTemplate: "{kind} {csv.name}" }]);
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    await userEvent.type(screen.getByTestId("integration-instance-id"), "office-inventory");
    await userEvent.type(screen.getByTestId("integration-setting-baseUrl"), "https://thing.invalid");
    await userEvent.type(screen.getByTestId("integration-setting-apiKey"), "secret");
    await userEvent.click(screen.getByTestId("integration-run"));

    await waitFor(() => expect(submitJobMock).toHaveBeenCalledTimes(1));
    const job = submitJobMock.mock.calls[0][1];
    expect(job.embedSummaries).toBeUndefined();
    // A name with the flag off would read on the wire as an opt-in nobody made.
    expect(job.embeddingName).toBeUndefined();
  });

  it("omits the name when left blank, so the runtime's own default applies", async () => {
    listProvidersMock.mockResolvedValue([{ ...fourthIntegration(), entitySummaryTemplate: "{kind} {csv.name}" }]);
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    await userEvent.type(screen.getByTestId("integration-instance-id"), "office-inventory");
    await userEvent.type(screen.getByTestId("integration-setting-baseUrl"), "https://thing.invalid");
    await userEvent.type(screen.getByTestId("integration-setting-apiKey"), "secret");
    await userEvent.click(await screen.findByTestId("integration-embed-toggle"));
    await userEvent.click(screen.getByTestId("integration-run"));

    await waitFor(() => expect(submitJobMock).toHaveBeenCalledTimes(1));
    expect(submitJobMock.mock.calls[0][1].embedSummaries).toBe(true);
    expect(submitJobMock.mock.calls[0][1].embeddingName).toBeUndefined();
  });

  it("is disabled and says so when the embedding provider is off on this instance", async () => {
    getStatusMock.mockResolvedValue(status(false));
    listProvidersMock.mockResolvedValue([{ ...fourthIntegration(), entitySummaryTemplate: "{kind} {csv.name}" }]);
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    const toggle = await screen.findByTestId("integration-embed-toggle");
    expect(toggle).toBeDisabled();
    // Dead-ending is the failure mode this replaces: the sentence names where the switch lives.
    expect(await screen.findByTestId("integration-embed-off")).toHaveTextContent(
      /embedding provider is off/,
    );
  });

  it("shows the template it will embed, so what lands is visible before the run and not after", async () => {
    listProvidersMock.mockResolvedValue([
      {
        ...fourthIntegration(),
        entitySummaryTemplate: "{kind} {arxml.name}, {arxml.descEn}, {arxml.descDe}, {arxml.unit}",
      },
    ]);
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));
    await userEvent.click(await screen.findByTestId("integration-embed-toggle"));

    expect(await screen.findByTestId("integration-embed-template")).toHaveTextContent(
      "{kind} {arxml.name}, {arxml.descEn}, {arxml.descDe}, {arxml.unit}",
    );
  });

  it("refuses a malformed embedding name in the form rather than after the graph writes commit", async () => {
    listProvidersMock.mockResolvedValue([
      { ...fourthIntegration(), entitySummaryTemplate: "{kind} {csv.name}" },
    ]);
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    await userEvent.type(screen.getByTestId("integration-instance-id"), "office-inventory");
    await userEvent.type(screen.getByTestId("integration-setting-baseUrl"), "https://thing.invalid");
    await userEvent.type(screen.getByTestId("integration-setting-apiKey"), "secret");
    await userEvent.click(await screen.findByTestId("integration-embed-toggle"));
    await userEvent.type(await screen.findByTestId("integration-embed-name"), "not a name");

    // The cost of learning this from the runtime's 400 is asymmetric: the graph writes commit first,
    // and a corrected re-run embeds nothing, so the recovery is a tabula rasa and a full re-import.
    expect(await screen.findByTestId("integration-embed-name-problem")).toBeInTheDocument();
    expect(screen.getByTestId("integration-run")).toBeDisabled();
  });

  it("reads 'not requested' rather than 0 for a run that never asked to embed", async () => {
    listProvidersMock.mockResolvedValue([{ ...fourthIntegration(), entitySummaryTemplate: "{kind} {csv.name}" }]);
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    await userEvent.type(screen.getByTestId("integration-instance-id"), "office-inventory");
    await userEvent.type(screen.getByTestId("integration-setting-baseUrl"), "https://thing.invalid");
    await userEvent.type(screen.getByTestId("integration-setting-apiKey"), "secret");
    await userEvent.click(screen.getByTestId("integration-run"));

    // A 0 here collapsed three different states, and read as "the run tried and found nothing" for
    // every run Studio has ever launched - since Studio could not ask at all.
    expect(await screen.findByTestId("report-embedded")).toHaveTextContent("not requested");
  });

  it("reads the count once the run DID ask", async () => {
    getRunMock.mockResolvedValue(finishedRun(report({ summariesEmbedded: 7 }), true));
    listProvidersMock.mockResolvedValue([{ ...fourthIntegration(), entitySummaryTemplate: "{kind} {csv.name}" }]);
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    await userEvent.type(screen.getByTestId("integration-instance-id"), "office-inventory");
    await userEvent.type(screen.getByTestId("integration-setting-baseUrl"), "https://thing.invalid");
    await userEvent.type(screen.getByTestId("integration-setting-apiKey"), "secret");
    await userEvent.click(await screen.findByTestId("integration-embed-toggle"));
    await userEvent.click(screen.getByTestId("integration-run"));

    expect(await screen.findByTestId("report-embedded")).toHaveTextContent("7");
  });
});

describe("a run is watched rather than awaited (feature integration-run-visibility)", () => {
  async function startARun() {
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));
    await userEvent.type(screen.getByTestId("integration-instance-id"), "office-inventory");
    await userEvent.type(screen.getByTestId("integration-setting-baseUrl"), "https://thing.invalid");
    await userEvent.type(screen.getByTestId("integration-setting-apiKey"), "secret");
    await userEvent.click(screen.getByTestId("integration-run"));
    await waitFor(() => expect(submitJobMock).toHaveBeenCalledTimes(1));
  }

  it("renders every phase, marking the one in flight and the ones already done", async () => {
    getRunMock.mockResolvedValue(runningRun("embed-summaries", 4320, 9478));
    await startARun();

    const panel = await screen.findByTestId("run-panel");
    expect(panel).toBeInTheDocument();
    // The whole list, not only what has been reported: an operator needs to see what is still to come.
    expect(screen.getByTestId("run-phase-observe")).toHaveAttribute("data-state", "done");
    expect(screen.getByTestId("run-phase-embed-summaries")).toHaveAttribute("data-state", "running");
    expect(screen.getByTestId("run-phase-reconcile")).toHaveAttribute("data-state", "pending");
  });

  it("shows the count for the phase that runs for hours, which is the whole point", async () => {
    getRunMock.mockResolvedValue(runningRun("embed-summaries", 4320, 9478));
    await startARun();

    // Without this, embedding is indistinguishable from a hang for hours.
    // Built the same way the component builds it: the thousands separator is the runtime locale's,
    // and hard-coding one makes this pass or fail by where the machine thinks it is.
    expect(await screen.findByTestId("run-count-embed-summaries")).toHaveTextContent(
      `${(4320).toLocaleString()} / ${(9478).toLocaleString()}`,
    );
    expect(screen.getByTestId("run-elapsed")).toHaveTextContent("3h 07m");
  });

  it("says the run continues without the page, because it does", async () => {
    getRunMock.mockResolvedValue(runningRun("observe", 0, 0));
    await startARun();

    expect(await screen.findByText(/continues on the server/)).toBeInTheDocument();
  });

  it("renders the report once the run ends, since that is where the report now comes from", async () => {
    getRunMock.mockResolvedValue(finishedRun(report({ elementsCreated: 12 })));
    await startARun();

    expect(await screen.findByTestId("report-created")).toHaveTextContent("12");
  });

  it("re-attaches to a run in flight on a fresh mount, without submitting anything", async () => {
    // A run outlives the request that started it, so reopening the screen has to find it again. The
    // identity is the durable handle, and it is persisted for exactly this.
    store().getState().setIntegrationWatch("office-inventory");
    getRunMock.mockResolvedValue(runningRun("embed-summaries", 100, 9478));

    renderScreen();

    expect(await screen.findByTestId("run-panel")).toBeInTheDocument();
    expect(submitJobMock).not.toHaveBeenCalled();
  });

  it("says so when the runtime has no slot for the identity, rather than showing an error", async () => {
    store().getState().setIntegrationWatch("forgotten");
    getRunMock.mockRejectedValue(new ApiError(404, "/integrations/run/forgotten", "not found"));

    renderScreen();

    expect(await screen.findByTestId("run-untracked")).toHaveTextContent(/not tracking a run/);
  });

  it("reports a run that ended with no report at all", async () => {
    getRunMock.mockResolvedValue({
      ...finishedRun(report()),
      report: null,
      error: "The graph refused the embedding write with 400",
    });
    await startARun();

    expect(await screen.findByTestId("run-error")).toHaveTextContent("400");
  });
});

describe("the run survives a reload, and a poll failure is not diagnosed as absence", () => {
  it("re-attaches through REHYDRATION, not just through an in-memory store", async () => {
    // The previous test for this seeded the live store, so it could never catch the actual defect: the
    // watch was written to local storage by partialize and then nulled by merge on the way back in, so a
    // real reload lost the run entirely. This goes through storage.
    localStorage.setItem(
      "f8.workspace.local",
      JSON.stringify({ state: { integrationWatch: "office-inventory" }, version: 0 }),
    );
    resetInstanceStoresForTests();
    getRunMock.mockResolvedValue(runningRun("embed-summaries", 100, 9478));

    renderScreen();

    expect(await screen.findByTestId("run-panel")).toBeInTheDocument();
    expect(submitJobMock).not.toHaveBeenCalled();
  });

  it("does not claim a run is untracked when the poll failed for another reason", async () => {
    // A 503 or a 401 says nothing about whether the run exists. Rendering the untracked copy for it
    // asserts a cause the answer never gave, and sends the operator looking for the wrong thing.
    store().getState().setIntegrationWatch("office-inventory");
    getRunMock.mockRejectedValue(new ApiError(503, "/integrations/run/office-inventory", "sidecar down"));

    renderScreen();

    expect(await screen.findByTestId("run-poll-error")).toBeInTheDocument();
    expect(screen.queryByTestId("run-untracked")).not.toBeInTheDocument();
  });

  it("stops rendering a stale run as in flight once the runtime says it has none", async () => {
    store().getState().setIntegrationWatch("office-inventory");
    getRunMock.mockRejectedValue(new ApiError(404, "/integrations/run/office-inventory", "no run"));

    renderScreen();

    expect(await screen.findByTestId("run-untracked")).toBeInTheDocument();
    expect(screen.queryByTestId("run-panel")).not.toBeInTheDocument();
  });
});

describe("a second run under one identity is not served from the first run's cache", () => {
  it("asks again for the new run id instead of replaying the finished one", async () => {
    // The finished previous run has running=false, so the poll interval is off. Keyed on the identity
    // alone, react-query served that cached run forever and presented its report as the new run's
    // outcome - the run appearing to have finished before it started.
    submitJobMock.mockResolvedValue({ ...accepted(), runId: "run-1" });
    getRunMock.mockResolvedValue(finishedRun(report({ elementsCreated: 1 })));

    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));
    await userEvent.type(screen.getByTestId("integration-instance-id"), "office-inventory");
    await userEvent.type(screen.getByTestId("integration-setting-baseUrl"), "https://thing.invalid");
    await userEvent.type(screen.getByTestId("integration-setting-apiKey"), "secret");
    await userEvent.click(screen.getByTestId("integration-run"));
    await waitFor(() => expect(screen.getByTestId("report-created")).toHaveTextContent("1"));

    const pollsAfterFirst = getRunMock.mock.calls.length;

    // Second run, same identity, different run id and a different outcome.
    submitJobMock.mockResolvedValue({ ...accepted(), runId: "run-2" });
    getRunMock.mockResolvedValue(finishedRun(report({ elementsCreated: 42 })));
    // The form forgets the secret once a run reports, and it is required - so without re-typing it the
    // button stays disabled and the second click does nothing.
    await userEvent.type(screen.getByTestId("integration-setting-apiKey"), "secret");
    await userEvent.click(screen.getByTestId("integration-run"));

    await waitFor(() => expect(getRunMock.mock.calls.length).toBeGreaterThan(pollsAfterFirst));
    await waitFor(() => expect(screen.getByTestId("report-created")).toHaveTextContent("42"));
  });
});

describe("a run in flight can be stopped (feature integration-run-lifecycle)", () => {
  /**
   * Re-attach rather than submit, because it is the shorter path to a watched run AND the stronger
   * assertion: the form's identity field is empty here, so a cancel that reached the right identity
   * can only have read it off the run.
   */
  function watchARun() {
    store().getState().setIntegrationWatch("office-inventory");
    renderScreen();
  }

  it("offers the control while the run is in flight", async () => {
    getRunMock.mockResolvedValue(runningRun("embed-summaries", 4320, 9478));
    watchARun();

    expect(await screen.findByTestId("integration-run-cancel")).toBeEnabled();
  });

  it("offers nothing to stop once the run has ended, because a finished run is not cancellable", async () => {
    getRunMock.mockResolvedValue(finishedRun(report()));
    watchARun();

    await screen.findByTestId("run-panel");
    // The runtime answers 404 for it, so offering the control would offer a button that only ever
    // reports its own uselessness.
    expect(screen.queryByTestId("integration-run-cancel")).toBeNull();
  });

  it("asks nothing on the first step, and cancels under the run's own identity on the second", async () => {
    getRunMock.mockResolvedValue(runningRun("write-elements", 12, 40));
    watchARun();

    await userEvent.click(await screen.findByTestId("integration-run-cancel"));
    // Two-step in place: a run costs hours and there is no resuming one that was stopped, so the
    // first click may not be the request.
    expect(cancelRunMock).not.toHaveBeenCalled();

    await userEvent.click(screen.getByTestId("integration-run-cancel-confirm"));

    await waitFor(() => expect(cancelRunMock).toHaveBeenCalledTimes(1));
    // The identity, not the form: cancelling under the wrong one would leave the real run going.
    expect(cancelRunMock.mock.calls[0][1]).toBe("office-inventory");
  });

  it("takes the request back when the second step is declined", async () => {
    getRunMock.mockResolvedValue(runningRun("write-elements", 12, 40));
    watchARun();

    await userEvent.click(await screen.findByTestId("integration-run-cancel"));
    await userEvent.click(screen.getByTestId("integration-run-cancel-keep"));

    expect(cancelRunMock).not.toHaveBeenCalled();
    expect(screen.getByTestId("integration-run-cancel")).toBeEnabled();
    expect(screen.queryByTestId("integration-run-cancel-confirm")).toBeNull();
  });

  it("shows the stop as PENDING while the run has not reached a safe point yet", async () => {
    getRunMock.mockResolvedValue(stoppingRun());
    watchARun();

    // The whole reason cancelRequested is surfaced: a stop during embedding waits for the chunk
    // already in the model, and on CPU inference that is long enough to read as "nothing happened".
    expect(await screen.findByTestId("run-cancelling")).toHaveTextContent(/safe point/);
    const control = screen.getByTestId("integration-run-cancel");
    expect(control).toBeDisabled();
    expect(control).toHaveTextContent("cancelling...");
  });

  it("reflects the pending stop as soon as the request is answered", async () => {
    getRunMock.mockResolvedValue(runningRun("embed-summaries", 4320, 9478));
    // What the runtime really does, and the reason the mock changes both answers at once: the flag
    // is recorded BEFORE the 202 is answered, so the answer and every poll after it agree. A mock
    // where only the answer knows about the stop would be testing a server contradicting itself.
    cancelRunMock.mockImplementation(async () => {
      getRunMock.mockResolvedValue(stoppingRun());
      return stoppingRun();
    });
    watchARun();

    await userEvent.click(await screen.findByTestId("integration-run-cancel"));
    await userEvent.click(screen.getByTestId("integration-run-cancel-confirm"));

    expect(await screen.findByTestId("run-cancelling")).toBeInTheDocument();
  });

  it("renders a cancelled run as its own terminal state, with its counts and the convergence note", async () => {
    getRunMock.mockResolvedValue(cancelledRun());
    watchARun();

    const note = await screen.findByTestId("run-cancelled");
    expect(note).toHaveTextContent("embed-summaries");
    // The load-bearing sentence: a cancelled run never reconciles, so it withdrew nothing, and the
    // graph is left convergent-on-next-run rather than damaged.
    expect(note).toHaveTextContent(/[Nn]othing was withdrawn or deleted/);
    expect(note).toHaveTextContent(/next completed run/);
    expect(screen.getByTestId("run-panel")).toHaveTextContent("run - cancelled");

    // The partial report is still the account of what happened, and it must not read as a failure:
    // a cancelled report deliberately carries no errorKind.
    expect(screen.getByTestId("report-created")).toHaveTextContent("6");
    expect(screen.getByTestId("report-matched")).toHaveTextContent("11");
    expect(screen.getByTestId("integration-report-cancelled")).toHaveTextContent(/not a failure/);
    expect(screen.queryByTestId("integration-report-error")).toBeNull();
  });

  it("renders the too-late stop as the completed run it is, not as a cancellation", async () => {
    // Reachable and not hypothetical: the stop arrived after the run's last safe point, so
    // cancelRequested stays true on a run that finished normally AND reconciled.
    getRunMock.mockResolvedValue({ ...finishedRun(report()), cancelRequested: true });
    watchARun();

    expect(await screen.findByTestId("run-cancel-too-late")).toHaveTextContent(/completed/);
    expect(screen.queryByTestId("run-cancelled")).toBeNull();
    expect(screen.queryByTestId("integration-report-cancelled")).toBeNull();
    expect(screen.getByTestId("run-panel")).toHaveTextContent("run - finished");
  });

  it("treats a 404 on the stop as information rather than as a broken panel", async () => {
    getRunMock.mockResolvedValue(runningRun("reconcile", 0, 0));
    cancelRunMock.mockRejectedValue(
      new ApiError(404, "/integrations/run/office-inventory/cancel", "no run in flight"),
    );
    watchARun();

    await userEvent.click(await screen.findByTestId("integration-run-cancel"));
    await userEvent.click(screen.getByTestId("integration-run-cancel-confirm"));

    // The run ended between the last poll and the click. Nothing is wrong, so an error box would
    // send the operator looking for a fault that does not exist.
    expect(await screen.findByTestId("run-cancel-already-ended")).toHaveTextContent(
      /already ended/,
    );
    expect(screen.queryByTestId("run-cancel-error")).toBeNull();
    expect(screen.getByTestId("run-panel")).toBeInTheDocument();
  });

  it("shows a real error box for a stop that failed for any other reason", async () => {
    getRunMock.mockResolvedValue(runningRun("reconcile", 0, 0));
    cancelRunMock.mockRejectedValue(
      new ApiError(503, "/integrations/run/office-inventory/cancel", "sidecar down"),
    );
    watchARun();

    await userEvent.click(await screen.findByTestId("integration-run-cancel"));
    await userEvent.click(screen.getByTestId("integration-run-cancel-confirm"));

    // A 503 says nothing about whether the run exists, so claiming it had ended would assert a
    // cause the answer never gave - and the run is in fact still going.
    expect(await screen.findByTestId("run-cancel-error")).toBeInTheDocument();
    expect(screen.queryByTestId("run-cancel-already-ended")).toBeNull();
  });
});

describe("a run picked up after a restart says so (feature integration-run-lifecycle)", () => {
  function watchARun() {
    store().getState().setIntegrationWatch("office-inventory");
    renderScreen();
  }

  it("says the run was picked up after a restart while it is still going", async () => {
    getRunMock.mockResolvedValue(resumedRun());
    watchARun();

    const note = await screen.findByTestId("run-resumed");
    // The elapsed figure spans the outage on purpose, so a panel that did not say so would look
    // like a run stuck for however long the runtime was down.
    expect(note).toHaveTextContent(/includes the outage/);
    expect(screen.getByTestId("run-panel")).toHaveTextContent("run - in flight");
  });

  it("keeps saying it once the run has finished, because that is where the counts mislead", async () => {
    getRunMock.mockResolvedValue({
      ...finishedRun(report({ elementsCreated: 0, elementsMatched: 39 })),
      resumed: true,
    });
    watchARun();

    // A resumed run reports only what happened after the pickup, so nothing created is a normal
    // outcome for it - and reads as a run that did nothing without this note.
    expect(await screen.findByTestId("run-resumed")).toBeInTheDocument();
    expect(screen.getByTestId("report-created")).toHaveTextContent("0");
    expect(screen.getByTestId("report-matched")).toHaveTextContent("39");
  });

  it("says nothing of the kind for an ordinary run", async () => {
    getRunMock.mockResolvedValue(runningRun("embed-summaries", 4320, 9478));
    watchARun();

    await screen.findByTestId("run-panel");
    expect(screen.queryByTestId("run-resumed")).toBeNull();
  });

  it("renders a run that could NOT be resumed as the finished, failed run it is", async () => {
    // The one interruption a pickup cannot recover from: the source had not been read yet, so there
    // is nothing to continue from. The runtime still reports it under the same identity with the
    // flag set, which is why the flag must not by itself imply a run in flight.
    getRunMock.mockResolvedValue({
      ...finishedRun(report()),
      resumed: true,
      report: null,
      error:
        "This run stopped before its source had been read, so there is nothing to continue from. Submit the job again.",
    });
    watchARun();

    expect(await screen.findByTestId("run-error")).toHaveTextContent(/Submit the job again\.$/);
    expect(screen.getByTestId("run-resumed")).toBeInTheDocument();
    expect(screen.getByTestId("run-panel")).toHaveTextContent("run - finished");
    // Neither of the two things it is not: still going, or stopped on request.
    expect(screen.queryByTestId("integration-run-cancel")).toBeNull();
    expect(screen.queryByTestId("run-cancelled")).toBeNull();
    expect(screen.queryByTestId("run-cancel-too-late")).toBeNull();
  });
});

/**
 * WHAT THE SEND LOOKS LIKE WHILE IT HAPPENS (feature integration-file-transport).
 *
 * The failure that prompted all of this: an operator staged several gigabytes of extracts, pressed
 * run, saw one unchanging word for minutes, and got an error. So a send now reports its progress,
 * can be cancelled, and never encodes a file into memory on the way.
 */
describe("sending a job is visible while it happens", () => {
  /** Selects the multi-file provider and fills everything a run needs except the files. */
  async function ready() {
    listProvidersMock.mockResolvedValue([multiFileIntegration()]);
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));
    await userEvent.type(screen.getByTestId("integration-instance-id"), "vehicle-network");
    await userEvent.type(screen.getByTestId("integration-setting-baseUrl"), "https://thing.invalid");
    await userEvent.type(screen.getByTestId("integration-setting-apiKey"), "secret");
  }

  function extract(name: string): File {
    return new File([`<AUTOSAR>${name}</AUTOSAR>`], name, { type: "application/xml" });
  }

  const staged = () => screen.getAllByTestId("integration-setting-extract-staged-file");

  /** Stages one extract, which is all most of these need. */
  async function stageOne() {
    await ready();
    await userEvent.upload(screen.getByTestId("integration-setting-extract"), [
      extract("body.arxml"),
    ]);
    await waitFor(() => expect(staged()).toHaveLength(1));
  }

  it("walks the button through starting, sending a share, and starting the run", async () => {
    let release: (value: IntegrationRunAccepted | null) => void = () => {};
    let report: SendOptions["onProgress"];
    submitJobMock.mockImplementation((_i, _job, options) => {
      report = options?.onProgress;
      return new Promise((resolve) => {
        release = resolve;
      });
    });

    await stageOne();

    const button = () => screen.getByTestId("integration-run");
    expect(button()).toHaveTextContent("run now");

    await userEvent.click(button());
    await waitFor(() => expect(submitJobMock).toHaveBeenCalledTimes(1));
    // Before the first progress event there is genuinely nothing to report, so the old word stands.
    expect(button()).toHaveTextContent("starting");

    await act(async () => {
      report!({ sent: 250, total: 1000 });
    });
    expect(button()).toHaveTextContent("sending 25%");
    expect(screen.getByTestId("integration-send-progress")).toHaveTextContent("250 B of 1000 B");

    // Everything sent, but the runtime has not answered: the label says starting the RUN rather than
    // claiming the run is under way, because this call ends when the job is ACCEPTED and the run
    // outlives it.
    await act(async () => {
      report!({ sent: 1000, total: 1000 });
    });
    expect(button()).toHaveTextContent("starting the run");

    await act(async () => {
      release(accepted());
    });
    await waitFor(() => expect(button()).toHaveTextContent("run now"));
    // And the progress row is gone rather than left showing a finished send.
    expect(screen.queryByTestId("integration-send-progress")).toBeNull();
  });

  it("reports bytes sent even when the browser will not say how many there are", async () => {
    let report: SendOptions["onProgress"];
    submitJobMock.mockImplementation((_i, _job, options) => {
      report = options?.onProgress;
      return new Promise(() => {});
    });

    await stageOne();
    await userEvent.click(screen.getByTestId("integration-run"));
    await waitFor(() => expect(submitJobMock).toHaveBeenCalledTimes(1));

    await act(async () => {
      report!({ sent: 4096, total: null });
    });

    // No percentage and no bar, because inventing a denominator would show one that never fills.
    expect(screen.getByTestId("integration-send-progress")).toHaveTextContent("sending 4.0 KiB");
    expect(screen.getByTestId("integration-run")).toHaveTextContent("sending");
    expect(screen.getByTestId("integration-run")).not.toHaveTextContent("%");
  });

  it("cancelling the send aborts it, watches nothing, and shows no error", async () => {
    let signal: AbortSignal | undefined;
    submitJobMock.mockImplementation((_i, _job, options) => {
      signal = options?.signal;
      return new Promise((_resolve, reject) => {
        options?.signal?.addEventListener("abort", () =>
          reject(new DOMException("The upload was cancelled.", "AbortError")),
        );
      });
    });

    await stageOne();
    await userEvent.click(screen.getByTestId("integration-run"));
    await waitFor(() => expect(submitJobMock).toHaveBeenCalledTimes(1));

    expect(signal?.aborted).toBe(false);
    await userEvent.click(screen.getByTestId("integration-send-cancel"));

    await waitFor(() => expect(signal?.aborted).toBe(true));
    // Nothing started, so nothing is watched and nothing is polled: arming the watch optimistically
    // would show the identity's PREVIOUS finished run as this run's outcome.
    expect(getRunMock).not.toHaveBeenCalled();
    // And no error box: somebody who pressed cancel does not need to be told that it failed.
    await waitFor(() => expect(screen.getByTestId("integration-run")).toHaveTextContent("run now"));
    expect(screen.queryByTestId("integration-run-error")).toBeNull();
  });

  it("arms the watch only once the runtime has accepted the job", async () => {
    let release: (value: IntegrationRunAccepted | null) => void = () => {};
    submitJobMock.mockImplementation(
      () =>
        new Promise((resolve) => {
          release = resolve;
        }),
    );

    await stageOne();
    await userEvent.click(screen.getByTestId("integration-run"));
    await waitFor(() => expect(submitJobMock).toHaveBeenCalledTimes(1));

    expect(getRunMock).not.toHaveBeenCalled();

    await act(async () => {
      release(accepted());
    });
    await waitFor(() => expect(getRunMock).toHaveBeenCalled());
  });

  /**
   * The point of the feature, asserted where it cannot be argued away: nothing base64-encodes a file
   * any more. `btoa` over a set of extracts is what capped a job at about 384 MiB - a JavaScript
   * string maxes at 512 MiB - whatever the instance would have accepted.
   */
  it("base64-encodes nothing at all across a three-file submit", async () => {
    const btoaSpy = vi.spyOn(globalThis, "btoa");

    await ready();
    await userEvent.upload(screen.getByTestId("integration-setting-extract"), [
      extract("body.arxml"),
      extract("chassis.arxml"),
      extract("powertrain.arxml"),
    ]);
    await waitFor(() => expect(staged()).toHaveLength(3));
    await userEvent.click(screen.getByTestId("integration-run"));
    await waitFor(() => expect(submitJobMock).toHaveBeenCalledTimes(1));

    expect(btoaSpy).not.toHaveBeenCalled();
    btoaSpy.mockRestore();
  });

  /**
   * Staging reads ONE BYTE and no more. The read exists to catch a handle that cannot be opened, at
   * the pick rather than minutes into a send; reading the whole file to learn that would restore the
   * memory cost this feature removed, so the SIZE of the read is what is asserted.
   */
  it("reads exactly one byte per file at staging time", async () => {
    const slices: Array<[number, number]> = [];
    const original = Blob.prototype.slice;
    const sliceSpy = vi
      .spyOn(Blob.prototype, "slice")
      .mockImplementation(function (this: Blob, start?: number, end?: number) {
        slices.push([start ?? 0, end ?? this.size]);
        return original.call(this, start, end);
      });

    await stageOne();

    expect(slices).toEqual([[0, 1]]);
    sliceSpy.mockRestore();
  });

  it("says a staged file is read at send time when the send fails", async () => {
    submitJobMock.mockRejectedValue(new TypeError("Failed to reach http://f8.test"));

    await stageOne();
    await userEvent.click(screen.getByTestId("integration-run"));

    const note = await screen.findByTestId("integration-send-stale-note");
    // The honest regression this transport introduces: a file moved or edited after staging fails
    // HERE rather than at the picker, so the copy has to say where to look.
    expect(note).toHaveTextContent("read while the job is sent");
    expect(note).toHaveTextContent("nothing was withdrawn");
  });

  it("names the missing capability when an instance refuses multipart outright", async () => {
    submitJobMock.mockRejectedValue(
      new ApiError(415, "/integrations/job", "Unsupported Media Type"),
    );

    await stageOne();
    await userEvent.click(screen.getByTestId("integration-run"));

    // Studio keeps no base64 fallback on purpose, so this is a real state an older instance can
    // produce, and it has to say what to do rather than show a bare 415.
    const note = await screen.findByTestId("integration-no-multipart");
    expect(note).toHaveTextContent("does not accept multipart");
  });
});
