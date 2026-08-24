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

import { useMemo, useRef, useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { useActiveInstance, useActiveNamespace } from "../instances/registry";
import { submitIntegrationJob } from "../api/endpoints";
import type {
  IntegrationJobReport,
  IntegrationJobRequest,
  IntegrationProvider,
  IntegrationSetting,
  SettingKind,
} from "../api/types";
import { capabilityOf, useIntegrationProviders } from "../state/integrations";
import { useEmbeddingProvider } from "../state/graphShape";
import { ApiError } from "../api/client";
import { ErrorBox } from "../components/ErrorBox";
import { FileDropzone } from "../components/FileDropzone";
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
  const [files, setFiles] = useState<Record<string, StagedFile>>({});
  const [fileProblems, setFileProblems] = useState<Record<string, string>>({});
  const [report, setReport] = useState<IntegrationJobReport | null>(null);
  const [embedSummaries, setEmbedSummaries] = useState(false);
  const [embeddingName, setEmbeddingName] = useState("");
  // What the run that produced `report` ASKED for. Without it the embedded tile cannot tell "the run
  // embedded nothing" from "nobody asked", and it read as the first for every run Studio ever launched.
  const [reportAskedToEmbed, setReportAskedToEmbed] = useState(false);

  const catalog = useMemo(() => providers.data ?? [], [providers.data]);
  const selected = catalog.find((provider) => provider.id === selectedId) ?? null;

  // The two halves of the opt-in that are NOT the operator's choice: a provider that declares no
  // template has no summary to render, and a target with no provider has nothing to render it with.
  // Either way the run would succeed and embed nothing, so the control is not offered rather than
  // offered and quietly ineffective.
  const summaryTemplate = selected?.entitySummaryTemplate?.trim() ?? "";
  const providerStats = useEmbeddingProvider(instance);
  const providerEnabled = providerStats === null ? null : providerStats.enabled;
  const canEmbed = summaryTemplate.length > 0 && providerEnabled === true;
  const embedRequested = canEmbed && embedSummaries;

  // Read by the async file staging below to find out whether the integration that asked for a file is
  // still the selected one. A ref and not the state value, because the closure captured the value as
  // it was when the read started, which is precisely the thing it needs to compare against.
  const selectedIdRef = useRef(selectedId);
  selectedIdRef.current = selectedId;

  const run = useMutation({
    mutationFn: () =>
      submitIntegrationJob(
        instance,
        buildJob(selected!, namespace, instanceId, values, files, embedRequested, embeddingName),
      ),
    onSuccess: (result) => {
      setReport(result);
      setReportAskedToEmbed(embedRequested);
      // The job has reported, so a secret typed into this form has done its work: drop it. Only on
      // success, and success here includes a run that FAILED - the report came back either way. A job
      // the runtime refused (400, 409, 503) never ran, so the form keeps its values for the retry
      // that follows a fixed setting.
      //
      // A staged FILE is deliberately kept. It is not a secret - it is the data the run wrote - and
      // re-running the same extract after fixing a setting is the common next action, so dropping it
      // would cost a second trip to the file picker for nothing. It is cleared on provider switch,
      // and it never leaves this tab except in the job.
      setValues((current) => forgetSecrets(selected, current));
    },
  });

  const identityProblem = describeIdentityProblem(instanceId);
  const missing = selected ? missingRequired(selected, values, files) : [];
  const canSubmit = selected !== null && identityProblem === null && missing.length === 0;

  function select(provider: IntegrationProvider) {
    setSelectedId(provider.id);
    setReport(null);
    run.reset();

    // Staged files are dropped with the provider that asked for them. Two integrations can declare
    // the same setting key, so keeping them would send one integration's extract to another.
    setFiles({});
    setFileProblems({});

    // The descriptor's own defaults, so a form opens on what the integration expects rather than on
    // blanks. Neither a credential nor a file setting ever carries one.
    const defaults: Record<string, string> = {};
    for (const setting of provider.settings) {
      if (setting.kind === "File" || setting.kind === "Credential") continue;
      if (setting.defaultValue) defaults[setting.key] = setting.defaultValue;
    }
    setValues(defaults);
  }

  /** Reads one picked or dropped file into memory, as BYTES. Nothing is sent until "run now". */
  async function stage(setting: IntegrationSetting, file: File) {
    // Which integration asked for it. Reading is asynchronous, so a big file picked just before
    // switching provider would otherwise land on the NEW one - the exact outcome select()'s reset
    // exists to prevent, arriving a moment too late for it.
    const askedBy = selectedId;

    setFileProblems((current) => withoutKey(current, setting.key));
    try {
      const bytes = await readBytes(file);
      if (askedBy !== selectedIdRef.current) return;

      if (bytes.length === 0) {
        // Refused here as well as by the runtime, because the round trip for this one is pure
        // latency: an empty file is the mistake somebody makes when they pick before saving.
        setFileProblems((current) => ({
          ...current,
          [setting.key]: `${file.name} is empty, so there would be nothing to read.`,
        }));
        return;
      }

      setFiles((current) => ({
        ...current,
        [setting.key]: { name: file.name, size: bytes.length, bytes },
      }));
    } catch (error) {
      if (askedBy !== selectedIdRef.current) return;
      setFileProblems((current) => ({
        ...current,
        [setting.key]: `${file.name} could not be read: ${describeReadFailure(error)}`,
      }));
    }
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
                file={files[setting.key]}
                problem={fileProblems[setting.key]}
                onFile={(picked) => void stage(setting, picked)}
                onClearFile={() => {
                  setFiles((current) => withoutKey(current, setting.key));
                  setFileProblems((current) => withoutKey(current, setting.key));
                }}
              />
            ))}

            {summaryTemplate.length > 0 && (
              <div className="space-y-1" data-testid="integration-embed">
                <label className="flex items-center gap-2 text-[12px]">
                  <input
                    type="checkbox"
                    data-testid="integration-embed-toggle"
                    checked={embedRequested}
                    disabled={!canEmbed}
                    title={
                      canEmbed
                        ? undefined
                        : providerEnabled === null
                          ? "provider status not reported by this server"
                          : "the embedding provider is off on this instance — set the Fallen8:Embedding section (F8_EMBEDDINGS under compose)"
                    }
                    onChange={(event) => setEmbedSummaries(event.target.checked)}
                  />
                  embed entity summaries
                </label>
                <span className="text-fg-faint block text-[11px]">
                  One vector per entity, so a semantic search finds things this run wrote by
                  meaning rather than by substring. Bind a vector index to the same name to search
                  them (Indexes).
                </span>
                {!canEmbed && (
                  <span className="text-warn block text-[11px]" data-testid="integration-embed-off">
                    {providerEnabled === null
                      ? "provider status not reported by this server — the run can still write the graph."
                      : "the embedding provider is off on this instance — the run can still write the graph."}
                  </span>
                )}
                {embedRequested && (
                  <>
                    <label className="block text-[12px]">
                      <span className="text-fg-faint block text-[10px] tracking-wide uppercase">
                        embedding name
                      </span>
                      <input
                        className="input w-full"
                        data-testid="integration-embed-name"
                        value={embeddingName}
                        placeholder="default"
                        onChange={(event) => setEmbeddingName(event.target.value)}
                      />
                    </label>
                    <span className="text-fg-faint block text-[11px]" data-testid="integration-embed-template">
                      embeds <code>{summaryTemplate}</code> per entity — a hole the entity cannot
                      fill collapses, so an entity with no description embeds only its name.
                    </span>
                    <span className="text-fg-faint block text-[11px]">
                      Only entities this run creates or changes are embedded. A graph already
                      imported without this cannot be embedded by re-running: clear the namespace
                      (tabula rasa) and run again.
                    </span>
                  </>
                )}
              </div>
            )}

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

            {run.isError && (
              <div className="space-y-1" data-testid="integration-run-error">
                <ErrorBox error={run.error} />
                {run.error instanceof ApiError && run.error.status === 413 && (
                  <p className="text-fg-dim text-[12px]">
                    The request body was refused before the run started - the file is larger than this
                    instance forwards. Nothing was read and nothing was withdrawn.
                  </p>
                )}
              </div>
            )}
          </div>
        </section>
      )}

      {report && <ReportPanel report={report} askedToEmbed={reportAskedToEmbed} />}
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
  /** File settings only: the file staged for this one, if any. */
  file?: StagedFile;
  /** File settings only: why the last pick could not be used. */
  problem?: string;
  /** File settings only: a file was picked or dropped. */
  onFile?: (file: File) => void;
  /** File settings only: forget the staged file. */
  onClearFile?: () => void;
}) {
  const { setting, value, onChange, file, problem, onFile, onClearFile } = props;
  const testid = `integration-setting-${setting.key}`;

  if (setting.kind === "Credential") {
    return (
      <CredentialField setting={setting} value={value} onChange={onChange} testid={testid} />
    );
  }

  if (setting.kind === "File") {
    return (
      <FileField
        setting={setting}
        file={file}
        problem={problem}
        onFile={onFile!}
        onClear={onClearFile!}
        testid={testid}
      />
    );
  }

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
      <span className="label-help">{setting.help}</span>
    </label>
  );
}

/**
 * A credential setting: the secret itself, and the only field this form forgets.
 *
 * The value is held for the run, dropped when it ends, and cleared from here the moment the job
 * reports. There is no second source and no credential store anywhere in the feature: whoever runs a
 * job is whoever already holds the credential.
 */
function CredentialField(props: {
  setting: IntegrationSetting;
  value: string;
  onChange: (value: string) => void;
  testid: string;
}) {
  const { setting, value, onChange, testid } = props;

  return (
    <label className="block">
      <span className="label">
        {setting.label}
        {setting.required && <span className="text-warn"> *</span>}
      </span>
      <input
        className="input"
        type="password"
        autoComplete="off"
        data-testid={testid}
        value={value}
        onChange={(event) => onChange(event.target.value)}
      />
      <span className="label-help">
        {setting.help} It is used for this run and then forgotten: nothing stores it, no report echoes
        it, and it is cleared from this form when the run reports. It does travel in the request, so a
        shared instance wants TLS.
      </span>
    </label>
  );
}

/**
 * A file setting: the file itself, taken the way the Knowledge screen takes a document.
 *
 * Dropping or picking STAGES the file rather than sending it - a run also needs an identity and the
 * other settings - and the file's bytes ride with the job when "run now" is pressed. It follows the
 * credential rule (see {@link CredentialField}) with one difference stated in the help: a file is not
 * a secret, so the form keeps it after a run instead of forgetting it.
 */
function FileField(props: {
  setting: IntegrationSetting;
  file?: StagedFile;
  problem?: string;
  onFile: (file: File) => void;
  onClear: () => void;
  testid: string;
}) {
  const { setting, file, problem, onFile, onClear, testid } = props;
  const pickRef = useRef<HTMLInputElement>(null);

  // A div and not a label: this field's controls are BUTTONS, and a label wrapping them makes its
  // static text activate the first one, which is not what clicking a caption should do.
  return (
    <div className="block">
      <span className="label">
        {setting.label}
        {setting.required && <span className="text-warn"> *</span>}
      </span>

      {/* The zone stays put once a file is staged, so a replacement can be DROPPED and not only
          picked. Swapping it for a plain row would leave the form with no drop target at all, and
          the second drop would land on the document and navigate away from the half-filled form. */}
      <FileDropzone testId={`${testid}-dropzone`} onFile={onFile}>
        {file ? (
          <span data-testid={`${testid}-staged`}>
            <span className="text-fg">{file.name}</span>{" "}
            <span className="text-fg-faint">({formatBytes(file.size)})</span> - drop another to
            replace it
          </span>
        ) : (
          <>
            Drop {setting.label.toLowerCase()} here
            {setting.accept ? ` (${setting.accept.split(",").join(" ")})` : ""}
          </>
        )}
      </FileDropzone>

      <div className="mt-2 flex items-center gap-2">
        <button
          type="button"
          className="btn"
          data-testid={`${testid}-pick`}
          onClick={() => pickRef.current?.click()}
        >
          {file ? "replace" : "pick a file"}
        </button>
        {file && (
          <button type="button" className="btn" data-testid={`${testid}-clear`} onClick={onClear}>
            remove
          </button>
        )}
      </div>

      <input
        ref={pickRef}
        type="file"
        className="hidden"
        accept={setting.accept ?? undefined}
        aria-label={setting.label}
        data-testid={testid}
        onChange={(event) => {
          const picked = event.target.files?.[0];
          // Cleared so that re-picking the same file fires a change again, which is what somebody
          // does after saving an edit to it.
          event.target.value = "";
          if (picked) onFile(picked);
        }}
      />

      {problem && (
        <span className="text-warn block text-[11px]" data-testid={`${testid}-problem`}>
          {problem}
        </span>
      )}

      <span className="label-help">
        {setting.help} It is read in your browser and travels with the run, so nothing is mounted and
        nothing is stored: the runtime drops it when the run ends. This tab keeps it for a re-run
        until you replace it, remove it, or pick another integration.
      </span>
    </div>
  );
}

/** The counts, the failure kind when there is one, and every diagnostic with its own code. */
function ReportPanel({
  report,
  askedToEmbed,
}: {
  report: IntegrationJobReport;
  askedToEmbed: boolean;
}) {
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
          {askedToEmbed ? (
            <Count label="embedded" value={report.summariesEmbedded ?? 0} testid="report-embedded" />
          ) : (
            <div>
              <div className="text-fg-faint text-[10px] tracking-wide uppercase">embedded</div>
              <div className="text-fg-faint" data-testid="report-embedded">
                not requested
              </div>
            </div>
          )}
          <div>
            <div className="text-fg-faint text-[10px] tracking-wide uppercase">wrote anything</div>
            <div data-testid="report-mutations">{report.issuedMutations ? "yes" : "no"}</div>
          </div>
        </div>
        <p className="text-fg-faint text-[11px]">
          took {report.durationMilliseconds} ms
          {report.credentialFingerprint
            ? `, credential fingerprint ${report.credentialFingerprint} (compare it with an earlier run from the same runtime process: the same fingerprint twice means the value you just changed never reached it)`
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

/**
 * Drops every credential from the form and keeps every ordinary setting. A base URL is not a secret,
 * and re-typing one after each run would be a nuisance for no gain.
 */
function forgetSecrets(
  provider: IntegrationProvider | null,
  values: Record<string, string>,
): Record<string, string> {
  if (!provider) return values;

  const kept = { ...values };
  for (const setting of provider.settings) {
    if (setting.kind === "Credential") {
      delete kept[setting.key];
    }
  }
  return kept;
}

/** One file held in this tab, waiting for a run. */
type StagedFile = { name: string; size: number; bytes: Uint8Array };

/**
 * Reads a file as BYTES, via FileReader.
 *
 * Bytes and not text, and FileReader rather than `file.text()`, for two independent reasons.
 *
 * Bytes, because the browser deciding the encoding loses information the runtime can still use: it
 * decodes with byte-order-mark detection, so an ARXML a vendor tool wrote as UTF-16 arrives intact
 * where `readAsText` would have made mojibake of it. That is the whole of what is claimed - a file in
 * a legacy codepage with NO byte-order mark still falls back to UTF-8 at the far end, exactly as it
 * did when the file came off a mount, so nothing here has made that case better or worse.
 *
 * FileReader, because jsdom does not implement `Blob.prototype.text`: it typechecks against the DOM
 * lib and then throws at test time.
 */
function readBytes(file: File): Promise<Uint8Array> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(reader.error ?? new Error("the file could not be read"));
    reader.onload = () => resolve(new Uint8Array(reader.result as ArrayBuffer));
    reader.readAsArrayBuffer(file);
  });
}

/** Bytes to base64, in chunks: one `String.fromCharCode(...bytes)` call blows the argument limit. */
function base64Of(bytes: Uint8Array): string {
  const chunk = 0x8000;
  let binary = "";
  for (let i = 0; i < bytes.length; i += chunk) {
    binary += String.fromCharCode(...bytes.subarray(i, i + chunk));
  }
  return btoa(binary);
}

function formatBytes(size: number): string {
  if (size < 1024) return `${size} B`;
  if (size < 1024 * 1024) return `${(size / 1024).toFixed(1)} KiB`;
  return `${(size / (1024 * 1024)).toFixed(1)} MiB`;
}

function describeReadFailure(error: unknown): string {
  return error instanceof Error && error.message ? error.message : "the browser did not say why";
}

function withoutKey<T>(map: Record<string, T>, key: string): Record<string, T> {
  const kept = { ...map };
  delete kept[key];
  return kept;
}

function inputType(kind: SettingKind): string {
  // Neither a credential nor a file setting reaches here: each has its own field, a password box and
  // a dropzone. Everything else is a value the form can render from the kind alone. A File case here
  // would be actively wrong - a controlled <input type="file" value=...> throws in React.
  switch (kind) {
    case "Number":
      return "number";
    case "Url":
      return "url";
    default:
      return "text";
  }
}

/**
 * Which required settings are still empty, named so the button's refusal is explainable. A file
 * setting is satisfied by a STAGED FILE and never by a typed value: the runtime refuses a file
 * setting named in `settings`, so a value-based check here would either always fail or invite
 * putting the name where it must not go.
 */
function missingRequired(
  provider: IntegrationProvider,
  values: Record<string, string>,
  files: Record<string, StagedFile>,
): string[] {
  return provider.settings
    .filter((setting) =>
      setting.required &&
      (setting.kind === "File"
        ? !files[setting.key]
        : !(values[setting.key] ?? "").trim()),
    )
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
 * The job, in three maps. A credential setting contributes to `credentialValues`, a file setting to
 * `files`, and everything else to `settings` - and neither a credential nor a file EVER to
 * `settings`. A setting is neither leased nor redacted by the runtime, so a secret there would be
 * logged and reported like any other value; and the runtime opens nothing on disk, so a file name
 * there would name a file nothing can read (it refuses such a job rather than trying).
 */
function buildJob(
  provider: IntegrationProvider,
  namespace: string,
  instanceId: string,
  values: Record<string, string>,
  staged: Record<string, StagedFile>,
  embedSummaries: boolean,
  embeddingName: string,
): IntegrationJobRequest {
  const settings: Record<string, string> = {};
  const credentialValues: Record<string, string> = {};
  const files: Record<string, { name: string; contentBase64: string }> = {};

  for (const setting of provider.settings) {
    if (setting.kind === "File") {
      const file = staged[setting.key];
      if (file) {
        // NOT trimmed, and not decoded to text on the way: the bytes are what the provider parses,
        // and the file's own name is what its messages will call it.
        files[setting.key] = { name: file.name, contentBase64: base64Of(file.bytes) };
      }

      continue;
    }

    const raw = values[setting.key] ?? "";
    if (!raw.trim()) continue;

    if (setting.kind !== "Credential") {
      settings[setting.key] = raw.trim();
      continue;
    }

    // NOT trimmed. A leading or trailing space can be part of a real password, and the runtime
    // deliberately preserves them (it drops exactly one trailing newline and nothing else). Trimming
    // here would produce an authentication failure from somebody's controller with nothing on the
    // report to explain it. Emptiness is judged on the trimmed form; what is sent is verbatim.
    credentialValues[setting.key] = raw;
  }

  return {
    providerId: provider.id,
    integrationInstanceId: instanceId.trim(),
    namespace,
    settings,
    credentialValues,
    files,
    // Both halves or neither. The runtime needs the flag AND a name, and sending a name with the
    // flag off would read on the wire as an opt-in the operator did not make.
    ...(embedSummaries
      ? { embedSummaries: true, ...(embeddingName.trim() ? { embeddingName: embeddingName.trim() } : {}) }
      : {}),
  };
}
