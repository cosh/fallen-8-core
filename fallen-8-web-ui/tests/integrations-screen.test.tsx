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
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { ApiError } from "../src/api/client";
import type { InstanceConfig } from "../src/instances/types";
import type {
  IntegrationJobReport,
  IntegrationJobRequest,
  IntegrationProvider,
} from "../src/api/types";
import { resetInstanceStoresForTests } from "../src/state/instanceStore";
import { capList, LIST_MAX_ROWS, SCROLL_ROWS } from "../src/lib/listCaps";
import { ListCapNote } from "../src/components/ListCapNote";

/**
 * Integrations screen (feature integrations). The load-bearing test is the first one: the form is
 * rendered from a DESCRIPTOR THE SCREEN HAS NEVER SEEN, which is what makes "adding a fourth
 * integration needs no Studio change" a fact rather than a claim. The rest pin the credential
 * field's two sources (the secret itself, which the form forgets once the job reports, or the name
 * of a mounted credential), the job split that keeps either of them out of `settings`, the identity
 * shape check that avoids a 400 rather than explaining it, the report, and the two ways an instance
 * says it has no runtime.
 */

const listProvidersMock = vi.fn<(i: InstanceConfig) => Promise<IntegrationProvider[] | null>>();
const submitJobMock =
  vi.fn<(i: InstanceConfig, job: IntegrationJobRequest) => Promise<IntegrationJobReport | null>>();

vi.mock("../src/api/endpoints", async (importOriginal) => {
  const original = await importOriginal<typeof import("../src/api/endpoints")>();
  return {
    ...original,
    listIntegrationProviders: (i: InstanceConfig) => listProvidersMock(i),
    submitIntegrationJob: (i: InstanceConfig, job: IntegrationJobRequest) => submitJobMock(i, job),
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
    ],
    entityKinds: ["thing"],
    claimTypes: ["mac"],
    relationTypes: [],
    canObserveCompleteState: true,
    readOnly: true,
  };
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
  submitJobMock.mockReset().mockResolvedValue(report());
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

describe("a credential setting takes the secret, and the form forgets it", () => {
  it("renders as a password box saying the value is used and then forgotten", async () => {
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    // The default source is the VALUE, because that is what somebody at a form has in hand.
    expect(screen.getByTestId("integration-setting-apiKey")).toHaveAttribute("type", "password");
    expect(screen.getByText(/used for this run and then forgotten/)).toBeInTheDocument();
  });

  it("sends the secret in credentialValues, never in settings and never in credentials", async () => {
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    await userEvent.type(screen.getByTestId("integration-instance-id"), "office-inventory");
    await userEvent.type(screen.getByTestId("integration-setting-baseUrl"), "https://thing.invalid");
    await userEvent.type(screen.getByTestId("integration-setting-apiKey"), "the-real-secret");
    await userEvent.click(screen.getByTestId("integration-run"));

    await waitFor(() => expect(submitJobMock).toHaveBeenCalledTimes(1));
    const job = submitJobMock.mock.calls[0][1];
    // A setting is neither leased nor redacted by the runtime, so a secret there would be logged and
    // reported like any other value. credentials is for NAMES, and a secret in it would be read as one.
    expect(job.credentialValues).toEqual({ apiKey: "the-real-secret" });
    expect(job.credentials).toEqual({});
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

  it("clears the secret once the job reports, and keeps a credential NAME", async () => {
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

  it("switches to a mounted credential NAME, which is a text field and goes in credentials", async () => {
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    await userEvent.type(screen.getByTestId("integration-instance-id"), "office-inventory");
    await userEvent.type(screen.getByTestId("integration-setting-baseUrl"), "https://thing.invalid");
    await userEvent.type(screen.getByTestId("integration-setting-apiKey"), "the-real-secret");
    await userEvent.click(screen.getByTestId("integration-setting-apiKey-source"));

    const field = screen.getByTestId("integration-setting-apiKey");
    expect(field).toHaveAttribute("type", "text");
    // The typed secret must NOT survive the switch: it would be submitted as a credential NAME, which
    // is the map that is safe to keep and to commit.
    expect(field).toHaveValue("");
    expect(screen.getByText(/NAME of a credential/)).toBeInTheDocument();

    await userEvent.type(field, "unifi-console");
    await userEvent.click(screen.getByTestId("integration-run"));

    await waitFor(() => expect(submitJobMock).toHaveBeenCalledTimes(1));
    const job = submitJobMock.mock.calls[0][1];
    expect(job.credentials).toEqual({ apiKey: "unifi-console" });
    expect(job.credentialValues).toEqual({});
  });

  it("keeps a credential NAME after the run, because a name is not a secret", async () => {
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    await userEvent.type(screen.getByTestId("integration-instance-id"), "office-inventory");
    await userEvent.type(screen.getByTestId("integration-setting-baseUrl"), "https://thing.invalid");
    await userEvent.click(screen.getByTestId("integration-setting-apiKey-source"));
    await userEvent.type(screen.getByTestId("integration-setting-apiKey"), "unifi-console");
    await userEvent.click(screen.getByTestId("integration-run"));

    await screen.findByTestId("report-created");
    expect(screen.getByTestId("integration-setting-apiKey")).toHaveValue("unifi-console");
  });

  it("never sends a job the runtime would refuse for having two sources", async () => {
    renderScreen();
    await userEvent.click(await screen.findByTestId("integration-select-hypothetical-fourth"));

    await userEvent.type(screen.getByTestId("integration-instance-id"), "office-inventory");
    await userEvent.type(screen.getByTestId("integration-setting-baseUrl"), "https://thing.invalid");
    await userEvent.type(screen.getByTestId("integration-setting-apiKey"), "the-real-secret");
    await userEvent.click(screen.getByTestId("integration-setting-apiKey-source"));
    await userEvent.type(screen.getByTestId("integration-setting-apiKey"), "unifi-console");
    await userEvent.click(screen.getByTestId("integration-run"));

    await waitFor(() => expect(submitJobMock).toHaveBeenCalledTimes(1));
    // One field, one source, whichever way it is switched: the runtime rejects a setting that arrives
    // in both maps, and a form that could compose one would turn a click into a 400.
    const job = submitJobMock.mock.calls[0][1];
    const both = Object.keys(job.credentials).filter((key) =>
      Object.prototype.hasOwnProperty.call(job.credentialValues ?? {}, key),
    );
    expect(both).toEqual([]);
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
    submitJobMock.mockResolvedValue(
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
