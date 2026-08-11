// MIT License
//
// IntegrationsScreen.tsx
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

import { useMemo, useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { useActiveInstance, useActiveNamespace } from "../instances/registry";
import { submitIntegrationJob } from "../api/endpoints";
import type {
  IntegrationJobReport,
  IntegrationProvider,
  IntegrationSetting,
  SettingKind,
} from "../api/types";
import { capabilityOf, useIntegrationProviders } from "../state/integrations";
import { ErrorBox } from "../components/ErrorBox";
import { ListCapNote } from "../components/ListCapNote";
import { Truncated } from "../components/Truncated";
import { DISPLAY_CAP } from "../lib/truncate";
import { capList, SCROLL_ROWS, scrollRows } from "../lib/listCaps";

/**
 * Integrations (feature integrations): the integrations this instance's runtime ships, a settings
 * form for the selected one, and the report of the run it submits.
 *
 * The form is rendered from the DESCRIPTOR alone - from each setting's kind, required flag and help
 * text - and there is deliberately no switch on provider id anywhere in this file. A provider that
 * needed its own component would be a contract failure rather than a UI task, and the agent that
 * writes the fourth integration cannot write a React component for it anyway.
 *
 * There is no schedule, no run history and no saved job list here, because the runtime keeps none:
 * timing belongs to whoever wants the data, and a Studio-side copy would be a second home for a
 * decision this screen cannot judge. Submitting is a one-shot action.
 */
export function IntegrationsScreen() {
  const instance = useActiveInstance()!;
  const namespace = useActiveNamespace();
  const providers = useIntegrationProviders(instance);
  const capability = capabilityOf(providers);

  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [instanceId, setInstanceId] = useState("");
  const [values, setValues] = useState<Record<string, string>>({});
  const [report, setReport] = useState<IntegrationJobReport | null>(null);

  const catalog = useMemo(() => providers.data ?? [], [providers.data]);
  const selected = catalog.find((provider) => provider.id === selectedId) ?? null;

  const run = useMutation({
    mutationFn: () => submitIntegrationJob(instance, buildJob(selected!, namespace, instanceId, values)),
    onSuccess: (result) => setReport(result),
  });

  const identityProblem = describeIdentityProblem(instanceId);
  const missing = selected ? missingRequired(selected, values) : [];
  const canSubmit = selected !== null && identityProblem === null && missing.length === 0;

  function select(provider: IntegrationProvider) {
    setSelectedId(provider.id);
    setReport(null);
    run.reset();

    // The descriptor's own defaults, so a form opens on what the integration expects rather than on
    // blanks. A credential setting never carries one.
    const defaults: Record<string, string> = {};
    for (const setting of provider.settings) {
      if (setting.defaultValue) defaults[setting.key] = setting.defaultValue;
    }
    setValues(defaults);
  }

  // ---- gate: the capability is absent, so the screen says so rather than showing an error ----
  // A deep link lands here even though the rail entry is hidden, and 403 (secured instance) and 401
  // (open instance) both mean the same thing: there is no runtime to talk to.
  if (capability === "absent") {
    return (
      <div className="mx-auto max-w-5xl space-y-4">
        <section className="panel">
          <h2 className="panel-title">Integrations</h2>
          <p className="text-fg-dim p-3 text-[12px]" data-testid="integrations-absent">
            This instance has no integrations runtime. It is a separate deployable, and in the compose
            environment it comes up by default; <code>F8_INTEGRATIONS=false</code> leaves it out and
            makes these routes refuse.
          </p>
        </section>
      </div>
    );
  }

  const listed = capList(catalog);

  return (
    <div className="mx-auto max-w-5xl space-y-4">
      <div className="flex items-center gap-2">
        <h1 className="text-fg flex min-w-0 items-baseline gap-1 text-sm font-bold tracking-wider uppercase">
          <span className="shrink-0">Integrations -</span>
          <Truncated text={instance.name} max={DISPLAY_CAP.name} />
        </h1>
        <span className="text-fg-faint ml-auto text-[11px]">
          writes into namespace {namespace}
        </span>
      </div>

      <section className="panel">
        <div className="panel-title">Available integrations</div>
        {providers.isError && capability === "unreachable" && (
          <div className="p-3">
            <ErrorBox error={providers.error} onRetry={() => providers.refetch()} />
          </div>
        )}
        <div className="scroll-list" style={scrollRows(SCROLL_ROWS.integrations)}>
          <table className="w-full text-[12px]">
            <thead>
              <tr>
                <th className="table-cell text-left">integration</th>
                <th className="table-cell text-left">reads</th>
                <th className="table-cell text-left">writes</th>
                <th className="table-cell" />
              </tr>
            </thead>
            <tbody>
              {listed.shown.length === 0 && (
                <tr>
                  <td className="table-cell text-fg-faint" colSpan={4}>
                    {providers.isPending ? "loading" : "no integrations"}
                  </td>
                </tr>
              )}
              {listed.shown.map((provider) => (
                <tr key={provider.id}>
                  <td className="table-cell font-medium">{provider.displayName}</td>
                  <td className="table-cell text-fg-dim">{provider.description}</td>
                  <td className="table-cell text-fg-dim">{provider.entityKinds.join(", ")}</td>
                  <td className="table-cell text-right">
                    <button
                      type="button"
                      className={provider.id === selectedId ? "btn btn-accent" : "btn"}
                      data-testid={`integration-select-${provider.id}`}
                      onClick={() => select(provider)}
                    >
                      configure
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <ListCapNote shown={listed.shown.length} total={listed.total} />
      </section>

      {selected && (
        <section className="panel">
          <div className="panel-title">Run {selected.displayName}</div>
          <div className="space-y-3 p-3">
            <label className="block">
              <span className="label">integration instance id</span>
              <input
                className="input"
                value={instanceId}
                data-testid="integration-instance-id"
                onChange={(event) => setInstanceId(event.target.value)}
                placeholder="office-inventory"
              />
              <span className="label-help">
                The identity this run asserts as, and it must be exactly the one this integration has
                always used. A fresh one each run leaves elements nothing will ever clean up; reusing
                another integration's identity withdraws and deletes what it claimed. Nothing can
                detect either afterwards. Letters, digits, dot, dash and underscore, at most 64.
              </span>
              {identityProblem && instanceId.length > 0 && (
                <span className="text-warn text-[11px]" data-testid="integration-instance-id-problem">
                  {identityProblem}
                </span>
              )}
            </label>

            {selected.settings.map((setting) => (
              <SettingField
                key={setting.key}
                setting={setting}
                value={values[setting.key] ?? ""}
                onChange={(next) => setValues((current) => ({ ...current, [setting.key]: next }))}
              />
            ))}

            <div className="flex items-center gap-2">
              <button
                type="button"
                className="btn btn-accent"
                data-testid="integration-run"
                disabled={!canSubmit || run.isPending}
                onClick={() => run.mutate()}
              >
                {run.isPending ? "running" : "run now"}
              </button>
              {missing.length > 0 && (
                <span className="text-fg-faint text-[11px]" data-testid="integration-missing">
                  needs {missing.join(", ")}
                </span>
              )}
            </div>

            {run.isError && <ErrorBox error={run.error} />}
          </div>
        </section>
      )}

      {report && <ReportPanel report={report} />}
    </div>
  );
}

/**
 * One setting, rendered from its KIND. An unknown kind falls back to a text input rather than
 * breaking the form, so an integration built against a newer runtime still runs from an older
 * Studio (the same tolerance the index-capability list uses).
 */
function SettingField(props: {
  setting: IntegrationSetting;
  value: string;
  onChange: (value: string) => void;
}) {
  const { setting, value, onChange } = props;
  const testid = `integration-setting-${setting.key}`;

  if (setting.kind === "Boolean") {
    return (
      <label className="block">
        <span className="label">
          {setting.label}
          {setting.required && <span className="text-warn"> *</span>}
        </span>
        <input
          type="checkbox"
          data-testid={testid}
          checked={value === "true"}
          onChange={(event) => onChange(event.target.checked ? "true" : "false")}
        />
        <span className="label-help">{setting.help}</span>
      </label>
    );
  }

  return (
    <label className="block">
      <span className="label">
        {setting.label}
        {setting.required && <span className="text-warn"> *</span>}
      </span>
      <input
        className="input"
        type={inputType(setting.kind)}
        data-testid={testid}
        value={value}
        onChange={(event) => onChange(event.target.value)}
      />
      <span className="label-help">
        {setting.kind === "Credential"
          ? `${setting.help} This is the NAME of a credential file the operator has put in the runtime's credential directory, never the secret itself: a value typed here would land in the job definition.`
          : setting.help}
      </span>
    </label>
  );
}

/** The counts, the failure kind when there is one, and every diagnostic with its own code. */
function ReportPanel({ report }: { report: IntegrationJobReport }) {
  const diagnostics = capList(report.diagnostics ?? []);

  return (
    <section className="panel">
      <div className="panel-title">Last run</div>
      <div className="space-y-3 p-3">
        {report.errorKind && (
          <p className="text-warn text-[12px]" data-testid="integration-report-error">
            failed ({report.errorKind}): {report.error}. Nothing was withdrawn, so the next run starts
            from the same graph.
          </p>
        )}
        <div className="grid grid-cols-2 gap-2 text-[12px] sm:grid-cols-4">
          <Count label="created" value={report.elementsCreated} testid="report-created" />
          <Count label="matched" value={report.elementsMatched} testid="report-matched" />
          <Count label="edges" value={report.edgesCreated} testid="report-edges" />
          <Count label="withdrawn" value={report.claimsWithdrawn} testid="report-withdrawn" />
          <Count label="deleted" value={report.elementsDeleted} testid="report-deleted" />
          <Count label="deferred" value={report.deletionsDeferred} testid="report-deferred" />
          <Count label="embedded" value={report.summariesEmbedded ?? 0} testid="report-embedded" />
          <div>
            <div className="text-fg-faint text-[10px] tracking-wide uppercase">wrote anything</div>
            <div data-testid="report-mutations">{report.issuedMutations ? "yes" : "no"}</div>
          </div>
        </div>
        <p className="text-fg-faint text-[11px]">
          took {report.durationMilliseconds} ms
          {report.credentialFingerprint
            ? `, credential fingerprint ${report.credentialFingerprint} (it changes when the file is overwritten in place, which is how a rotation is confirmed)`
            : ""}
        </p>

        {diagnostics.total > 0 && (
          <>
            <div className="scroll-list" style={scrollRows(SCROLL_ROWS.diagnostics)}>
              <table className="w-full text-[12px]">
                <thead>
                  <tr>
                    <th className="table-cell text-left">code</th>
                    <th className="table-cell text-left">subject</th>
                    <th className="table-cell text-left">message</th>
                  </tr>
                </thead>
                <tbody>
                  {diagnostics.shown.map((diagnostic, index) => (
                    <tr key={`${diagnostic.code}-${index}`}>
                      <td className="table-cell font-mono" data-testid="report-diagnostic-code">
                        {diagnostic.code}
                      </td>
                      <td className="table-cell text-fg-dim">{diagnostic.subject ?? ""}</td>
                      <td className="table-cell text-fg-dim">{diagnostic.message}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <ListCapNote shown={diagnostics.shown.length} total={diagnostics.total} />
          </>
        )}
      </div>
    </section>
  );
}

function Count(props: { label: string; value: number; testid: string }) {
  return (
    <div>
      <div className="text-fg-faint text-[10px] tracking-wide uppercase">{props.label}</div>
      <div data-testid={props.testid}>{props.value.toLocaleString()}</div>
    </div>
  );
}

function inputType(kind: SettingKind): string {
  // A CREDENTIAL setting is a NAME, so it is a plain text field: a password box would invite
  // somebody to type the secret, and the secret must never enter a job definition.
  switch (kind) {
    case "Number":
      return "number";
    case "Url":
      return "url";
    default:
      return "text";
  }
}

/** Which required settings are still empty, named so the button's refusal is explainable. */
function missingRequired(provider: IntegrationProvider, values: Record<string, string>): string[] {
  return provider.settings
    .filter((setting) => setting.required && !(values[setting.key] ?? "").trim())
    .map((setting) => setting.label);
}

/**
 * The shape rule, applied here so the runtime's 400 is avoided rather than explained. It is the same
 * allow-list the runtime enforces: the value is substituted into property and claim keys, so a
 * colon, at sign, pipe or dollar would let two identities compose one identical key.
 */
function describeIdentityProblem(instanceId: string): string | null {
  const value = instanceId.trim();
  if (value.length === 0) return "required";
  if (value.length > 64) return "at most 64 characters";
  if (!/^[A-Za-z0-9._-]+$/.test(value)) {
    return "letters, digits, dot, dash and underscore only";
  }
  return null;
}

/**
 * The job. A credential setting contributes its credential NAME to `credentials`; everything else is
 * a plain setting. The split is the whole reason a credential value never reaches a job definition.
 */
function buildJob(
  provider: IntegrationProvider,
  namespace: string,
  instanceId: string,
  values: Record<string, string>,
) {
  const settings: Record<string, string> = {};
  const credentials: Record<string, string> = {};

  for (const setting of provider.settings) {
    const value = (values[setting.key] ?? "").trim();
    if (!value) continue;
    if (setting.kind === "Credential") {
      credentials[setting.key] = value;
    } else {
      settings[setting.key] = value;
    }
  }

  return {
    providerId: provider.id,
    integrationInstanceId: instanceId.trim(),
    namespace,
    settings,
    credentials,
  };
}
