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
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useActiveInstance, useActiveNamespace, useInstanceStore } from "../instances/registry";
import { cancelIntegrationRun, getIntegrationRun, submitIntegrationJob } from "../api/endpoints";
import type { UploadProgress } from "../api/client";
import type { InstanceConfig } from "../instances/types";
import type {
  IntegrationJobFile,
  IntegrationJobReport,
  IntegrationJobRequest,
  IntegrationRunState,
  IntegrationProvider,
  IntegrationSetting,
  SettingKind,
} from "../api/types";
import { capabilityOf, useIntegrationLimits, useIntegrationProviders } from "../state/integrations";
import { useEmbeddingProvider } from "../state/graphShape";
import { ApiError, wasCancelled } from "../api/client";
import { ErrorBox } from "../components/ErrorBox";
import { FileDropzone } from "../components/FileDropzone";
import { ListCapNote } from "../components/ListCapNote";
import { Truncated } from "../components/Truncated";
import { formatBytes } from "../lib/format";
import { checkStaging, describeLimits } from "../lib/fileLimits";
import { DISPLAY_CAP } from "../lib/truncate";
import { capList, SCROLL_ROWS, scrollRows } from "../lib/listCaps";
import { RUN_PHASES } from "../api/types";

/**
 * Integrations (feature integrations): the integrations this instance's runtime ships, a settings
 * form for the selected one, and the report of the run it submits.
 *
 * The form is rendered from the DESCRIPTOR alone - from each setting's kind, required flag and help
 * text - and there is deliberately no switch on provider id anywhere in this file. A provider that
 * needed its own component would be a contract failure rather than a UI task, and the agent that
 * writes the fourth integration cannot write a React component for it anyway.
 *
 * Submitting STARTS a run and does not wait for it: the runtime answers a run id, and this screen
 * then watches that run - the phase it is in, and its report once it ends. That is not a run history
 * (the boundary is on IntegrationRunState), so there is no schedule, no list of past runs and no
 * saved job list here; timing belongs to whoever wants the data.
 */
export function IntegrationsScreen() {
  const instance = useActiveInstance()!;
  const { store } = useInstanceStore();
  const namespace = useActiveNamespace();
  const providers = useIntegrationProviders(instance);
  const capability = capabilityOf(providers);
  const queryClient = useQueryClient();

  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [instanceId, setInstanceId] = useState("");
  const [values, setValues] = useState<Record<string, string>>({});
  // A LIST per setting even where the setting takes one file, so nothing here branches on the
  // descriptor twice: `multiple` decides what may be added and what shape the job carries, and one
  // file is a list of one everywhere in between.
  const [files, setFiles] = useState<Record<string, StagedFile[]>>({});
  const [fileProblems, setFileProblems] = useState<Record<string, string>>({});
  // PERSISTED, so reopening the screen re-attaches to a run instead of losing it. A run can take
  // hours and deliberately outlives the request that started it, so "which identity am I watching"
  // is the only durable handle on it.
  const watching = store((state) => state.integrationWatch);
  const setWatching = store((state) => state.setIntegrationWatch);
  const [embedSummaries, setEmbedSummaries] = useState(false);
  const [embeddingName, setEmbeddingName] = useState("");
  // What the run that produced `report` ASKED for. Without it the embedded tile cannot tell "the run
  // embedded nothing" from "nobody asked", and it read as the first for every run Studio ever launched.
  // The run id the last submit was told to expect, so a re-run under one identity is not served from
  // the previous run's cache.
  const [expectedRunId, setExpectedRunId] = useState<string | null>(null);
  const submitStartedRef = useRef(false);

  // What a job may carry HERE, read once so a set that cannot be sent is refused in this form
  // rather than after the upload. Refusing at the far end works, but Kestrel drains the rest of the
  // body before the answer is read, so it costs the whole upload first (see the feature's
  // findings.md). Unknown is a real state - an older instance, or the capability off - and it means
  // check NOTHING: a fallback number here is the bug this feature exists to remove.
  // ONE value, read by both the checks and the copy. A second derived "is it unknown" flag was wrong
  // in the state that matters least often and confuses most: a failed REFETCH over retained data
  // leaves the query in error while the numbers it already answered with are still there, so the copy
  // said nothing was being checked while checkStaging was still checking against them.
  const limits = useIntegrationLimits(instance).data ?? undefined;

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
  // Checked HERE rather than left to the runtime's 400, because the cost of learning it late is
  // asymmetric: the graph writes commit before the embedding write is attempted, and a corrected
  // re-run embeds nothing (an unchanged source has no dirty summaries), so the recovery for a typo
  // is a tabula rasa and a full re-import.
  const embeddingNameProblem =
    embedRequested && embeddingName.trim() && !/^[A-Za-z0-9_-]{1,64}$/.test(embeddingName.trim())
      ? "letters, digits, dash and underscore only, at most 64"
      : null;

  // Read by the async file staging below to find out whether the integration that asked for a file is
  // still the selected one. A ref and not the state value, because the closure captured the value as
  // it was when the read started, which is precisely the thing it needs to compare against.
  const selectedIdRef = useRef(selectedId);
  selectedIdRef.current = selectedId;

  // How far the SEND has got, and the handle that stops it. Both are refs plus state on purpose:
  // the controller must survive re-renders (a state-only controller would be replaced mid-send and
  // the cancel button would abort nothing), while the bytes have to re-render a progress bar.
  const [sent, setSent] = useState<UploadProgress | null>(null);
  const uploadRef = useRef<AbortController | null>(null);
  // Whether the request still has bytes to go out. Only knowable when the browser told us a total;
  // with an unknown total the honest answer is "not knowably outstanding", which is why the strong
  // "nothing was sent" claim is withheld in that case rather than guessed at.
  const bytesOutstanding = sent !== null && sent.total !== null && sent.sent < sent.total;
  const fullySent = sent !== null && sent.total !== null && sent.sent >= sent.total;

  // The job and the identity are CAPTURED AT THE CLICK and travel as the mutation's variables, never
  // read again from the render closure. react-query keeps the latest render's callbacks, so an
  // onSuccess reading `instanceId` would arm the watch on whatever the field says when the upload
  // ENDS - and an upload here runs for minutes. Editing the field mid-send then left the run
  // untrackable: watched under a name no run exists for, so the run panel never rendered, so the stop
  // button inside it never rendered either, while a re-submit was refused 409 by the run gate. A
  // multi-hour run, invisible and unstoppable, from one keystroke.
  const submit = useMutation({
    mutationFn: (submitted: { job: IntegrationJobRequest; identity: string }) => {
      const controller = new AbortController();
      uploadRef.current = controller;
      setSent({ sent: 0, total: null });
      return submitIntegrationJob(instance, submitted.job, {
        signal: controller.signal,
        onProgress: setSent,
      });
    },
    onSettled: () => {
      uploadRef.current = null;
      setSent(null);
    },
    onSuccess: (accepted, submitted) => {
      // The answer is a run id, not a report. The identity is what survives a reload, because it is what
      // the runtime keys its slot by; the run id is what tells this run apart from the identity's last.
      setWatching(submitted.identity);
      setExpectedRunId(accepted?.runId ?? null);
      submitStartedRef.current = true;
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

  // Polled while the run is in flight and left alone once it ends, so a finished run costs nothing.
  // A 404 means this runtime has no slot for the identity (a restart, or enough other identities
  // since), which is a fact to render rather than an error to retry.
  // The expected run id is part of the key, so a SECOND run under the same identity is a different
  // query rather than a cache hit. Without it the screen served the finished previous run from cache,
  // never refetched (its `running` was false, so the interval was off), and presented the old run's
  // report as the new run's outcome.
  const runQueryKey = [instance.id, "integration-run", watching, expectedRunId];
  const runQuery = useQuery({
    queryKey: runQueryKey,
    queryFn: () => getIntegrationRun(instance, watching!),
    enabled: watching !== null,
    retry: false,
    refetchInterval: (query) =>
      (query.state.data as IntegrationRunState | undefined)?.running ? 2000 : false,
  });

  // A 404 is authoritative: the runtime has no slot for this identity, so whatever is cached is stale
  // and must not keep rendering as a live run. Anything else is an error about the REQUEST, not about
  // the run, and claiming "not tracked" for a 503 or a 401 would assert a cause the answer never gave.
  const untracked = runQuery.error instanceof ApiError && runQuery.error.status === 404;
  const run = untracked ? null : (runQuery.data ?? null);
  const report = run?.report ?? null;
  // A run started but not yet visible: the slot appears on its first phase, so a poll can 404 for a
  // moment right after a submit. Only a watch we did not start is genuinely untracked.
  const startedHere = submitStartedRef.current;

  const identityProblem = describeIdentityProblem(instanceId);
  const missing = selected ? missingRequired(selected, values, files) : [];
  const canSubmit =
    selected !== null &&
    identityProblem === null &&
    missing.length === 0 &&
    embeddingNameProblem === null;

  function select(provider: IntegrationProvider) {
    setSelectedId(provider.id);
    // Not the watch: a run in flight stays watchable while the operator looks at another integration.
    submit.reset();

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

  /** Reads picked or dropped files into memory, as BYTES. Nothing is sent until "run now". */
  async function stage(setting: IntegrationSetting, picked: File[]) {
    // Which integration asked for them. Reading is asynchronous, so a big file picked just before
    // switching provider would otherwise land on the NEW one - the exact outcome select()'s reset
    // exists to prevent, arriving a moment too late for it.
    const askedBy = selectedId;
    const multiple = setting.multiple === true;

    setFileProblems((current) => withoutKey(current, setting.key));

    // What a name has to be unique against: the files a multiple setting already holds, since a
    // second drop adds to them rather than replacing them, plus the ones accepted out of this batch.
    // Case is set aside, which is how the runtime compares - so 'Body.arxml' beside 'body.arxml' is
    // caught here instead of costing the whole job a 400.
    const taken = new Set(
      (multiple ? (files[setting.key] ?? []) : []).map((file) => file.name.trim().toLowerCase()),
    );
    const accepted: StagedFile[] = [];
    const problems: string[] = [];

    // The instance's OWN three ceilings, applied to what is being added together with what every
    // other file setting of this job already holds: the total and the count are job-wide, so a
    // per-setting check here would pass a job the instance then refuses.
    const verdict = checkStaging({
      limits,
      incoming: multiple ? picked : picked.slice(0, 1),
      staged: multiple ? (files[setting.key] ?? []) : [],
      elsewhere: Object.entries(files)
        .filter(([key]) => key !== setting.key)
        .flatMap(([, held]) => held),
      claimedSet: multiple,
    });
    if (verdict.problem) {
      problems.push(verdict.problem);
    }

    for (const file of verdict.accepted) {
      if (file.size === 0) {
        // Refused here as well as by the runtime, because the round trip for this one is pure
        // latency: an empty file is the mistake somebody makes when they pick before saving.
        // `size` is on the handle, so this costs no read at all.
        problems.push(`${file.name} is empty, so there would be nothing to read.`);
        continue;
      }

      try {
        await probeReadable(file);
      } catch (error) {
        if (askedBy !== selectedIdRef.current) return;
        problems.push(`${file.name} could not be read: ${describeReadFailure(error)}`);
        continue;
      }
      if (askedBy !== selectedIdRef.current) return;

      const name = file.name.trim().toLowerCase();
      if (taken.has(name)) {
        problems.push(`${file.name} is already staged here, and one name cannot mean two files.`);
        continue;
      }
      taken.add(name);
      accepted.push({ name: file.name, size: file.size, file });
    }

    if (accepted.length > 0) {
      setFiles((current) => ({
        ...current,
        // APPENDED for a multiple setting: a handover is read domain by domain, so somebody adds
        // the second extract to the first, and replacing the list would throw away the pick they
        // already made without saying so.
        [setting.key]: multiple ? [...(current[setting.key] ?? []), ...accepted] : accepted,
      }));
    }
    if (problems.length > 0) {
      setFileProblems((current) => ({ ...current, [setting.key]: problems.join(" ") }));
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
              {/* Frozen while the job is going out. The job already carries the identity captured at
                  the click, so an edit here could not change the run - it could only make the field
                  disagree with the run in progress, which is a reading nobody should have to reconcile
                  on the one field that decides what gets withdrawn. */}
              <input
                className="input"
                value={instanceId}
                data-testid="integration-instance-id"
                disabled={submit.isPending}
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
                files={files[setting.key] ?? []}
                problem={fileProblems[setting.key]}
                ceilings={describeLimits(limits)}
                onFiles={(picked) => void stage(setting, picked)}
                onRemoveFile={(index) => {
                  setFiles((current) => {
                    const kept = (current[setting.key] ?? []).filter((_, at) => at !== index);
                    // The key GOES when the last file does, rather than staying as an empty list: a
                    // required file setting is judged on there being a file, and an empty entry
                    // would answer that question with the wrong answer.
                    return kept.length > 0
                      ? { ...current, [setting.key]: kept }
                      : withoutKey(current, setting.key);
                  });
                  setFileProblems((current) => withoutKey(current, setting.key));
                }}
                onClearFiles={() => {
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
                          : "the embedding provider is off on this instance - set the Fallen8:Embedding section (F8_EMBEDDINGS under compose)"
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
                      ? "provider status not reported by this server - the run can still write the graph."
                      : "the embedding provider is off on this instance - the run can still write the graph."}
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
                      {embeddingNameProblem && (
                        <span className="text-warn text-[11px]" data-testid="integration-embed-name-problem">
                          {embeddingNameProblem}
                        </span>
                      )}
                    </label>
                    <span className="text-fg-faint block text-[11px]" data-testid="integration-embed-template">
                      embeds <code>{summaryTemplate}</code> per entity - a hole the entity cannot
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
                disabled={!canSubmit || submit.isPending}
                onClick={() =>
                  submit.mutate({
                    job: buildJob(
                      selected!,
                      namespace,
                      instanceId,
                      values,
                      files,
                      embedRequested,
                      embeddingName,
                    ),
                    // Folded the way the runtime folds it, so the watch and the run agree on the
                    // identity even if the field is edited while the body is still going out.
                    identity: instanceId.trim().toLowerCase(),
                  })
                }
              >
                {sendLabel(submit.isPending, sent)}
              </button>
              {/* Offered only while bytes are still OUTSTANDING. Once the body is fully sent the
                  runtime may already have accepted the job and started the run, and the run
                  deliberately does not die with its caller, so aborting the request from here would
                  stop nothing while looking like it stopped everything. From that point the run's own
                  panel is what stops it. */}
              {submit.isPending && !fullySent && (
                <button
                  type="button"
                  className="btn"
                  data-testid="integration-send-cancel"
                  onClick={() => uploadRef.current?.abort()}
                >
                  cancel
                </button>
              )}
              {missing.length > 0 && (
                <span className="text-fg-faint text-[11px]" data-testid="integration-missing">
                  needs {missing.join(", ")}
                </span>
              )}
            </div>

            {submit.isPending && sent !== null && (
              <div className="space-y-1" data-testid="integration-send-progress">
                {sent.total !== null && sent.total > 0 && (
                  <div className="bg-surface-2 h-1 w-full overflow-hidden rounded">
                    <div
                      className="bg-accent h-1"
                      style={{ width: `${Math.min(100, Math.round((sent.sent / sent.total) * 100))}%` }}
                    />
                  </div>
                )}
                <p className="text-fg-faint text-[11px]">
                  {sent.total !== null && sent.total > 0
                    ? `sending ${formatBytes(sent.sent)} of ${formatBytes(sent.total)}`
                    : `sending ${formatBytes(sent.sent)}`}
                  .{" "}
                  {/* The strong claim is made ONLY while bytes are outstanding, because only then is
                      it true. Once everything is sent the runtime may already have started the run,
                      and a run outlives the request that began it. */}
                  {bytesOutstanding
                    ? "Cancelling now sends nothing and starts nothing."
                    : "Everything has been sent and the runtime is deciding. If it starts the run, " +
                      "it is stopped from the run panel below rather than from here."}
                </p>
              </div>
            )}

            {/* An abort is not a failure. react-query moves an aborted MUTATION to isError with the
                abort as its error, so without this guard pressing cancel puts a red box reading
                "Cannot continue" in front of the person who pressed it. */}
            {submit.isError && !wasCancelled(submit.error) && (
              <div className="space-y-1" data-testid="integration-run-error">
                <ErrorBox error={submit.error} />
                {submit.error instanceof ApiError && submit.error.status === 413 && (
                  <p className="text-fg-dim text-[12px]">
                    The request body was refused before the run started - what it carries is larger
                    than this instance forwards, whether that is one file or the set of them.
                    Nothing was read and nothing was withdrawn.
                  </p>
                )}
                {submit.error instanceof ApiError && submit.error.status === 415 && (
                  <p className="text-fg-dim text-[12px]" data-testid="integration-no-multipart">
                    This instance does not accept multipart integration jobs, which is the only shape
                    this screen sends. It predates the file transport this version uses; upgrade it,
                    or submit the job over the API directly.
                  </p>
                )}
                {Object.keys(files).length > 0 && (
                  <p className="text-fg-faint text-[11px]" data-testid="integration-send-stale-note">
                    A staged file is read while the job is sent, not when it is picked, so one moved,
                    renamed or edited since fails here. Re-pick it if that is what happened. Nothing
                    started, so nothing was withdrawn.
                  </p>
                )}
              </div>
            )}
          </div>
        </section>
      )}

      {run && (
        <RunPanel
          // Keyed on the run, so a half-armed stop or a note about the LAST run cannot greet the
          // next one: a second run under one identity reuses this panel otherwise.
          key={run.runId}
          run={run}
          instance={instance}
          onCancelAnswered={(answered) => {
            // The 202 carries the run as it was when the stop was recorded, which is why the route
            // answers a body at all: the pending state shows without waiting out the poll interval.
            // The invalidate right after keeps the poll the authority on what the run is doing.
            if (answered) queryClient.setQueryData(runQueryKey, answered);
            void queryClient.invalidateQueries({ queryKey: runQueryKey });
          }}
        />
      )}
      {watching !== null && untracked && !startedHere && (
        <section className="panel" data-testid="run-untracked">
          <div className="panel-title">run</div>
          <p className="text-fg-faint px-3 pb-3 text-[11px]">
            This runtime is not tracking a run for '{watching}'. It has not run in this process, or a
            restart or enough other identities have displaced it. Nothing is wrong with the graph -
            the runtime keeps only the current and most recent run per identity, in memory.
          </p>
        </section>
      )}
      {watching !== null && runQuery.isError && !untracked && (
        <section className="panel" data-testid="run-poll-error">
          <div className="panel-title">run</div>
          <div className="px-3 pb-3">
            {/* NOT "not tracked": this answer says nothing about whether the run exists. */}
            <ErrorBox error={runQuery.error} />
          </div>
        </section>
      )}
      {report && <ReportPanel report={report} askedToEmbed={run?.embedRequested === true} />}
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
  /** File settings only: what is staged for this one, in the order it will be sent. */
  files?: StagedFile[];
  /** File settings only: why the last pick could not be used. */
  problem?: string;
  /** File settings only: what this instance accepts, in one line. */
  ceilings?: string;
  /** File settings only: files were picked or dropped. */
  onFiles?: (files: File[]) => void;
  /** File settings only: forget the staged file at this position. */
  onRemoveFile?: (index: number) => void;
  /** File settings only: forget all of them. */
  onClearFiles?: () => void;
}) {
  const { setting, value, onChange, files, problem, ceilings, onFiles, onRemoveFile, onClearFiles } =
    props;
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
        files={files ?? []}
        problem={problem}
        ceilings={ceilings ?? ""}
        onFiles={onFiles!}
        onRemove={onRemoveFile!}
        onClear={onClearFiles!}
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
 *
 * A setting the descriptor declares `multiple` takes a SET instead, and the set is what differs: a
 * pick adds to it rather than replacing it, each file has its own row and its own remove, and the
 * total is shown beside them because the job's total is a ceiling of its own. The order the rows are
 * in is the order the job carries, which a composing provider reads as precedence.
 */
function FileField(props: {
  setting: IntegrationSetting;
  files: StagedFile[];
  problem?: string;
  /** What this instance accepts, stated up front rather than only when refusing. */
  ceilings: string;
  onFiles: (files: File[]) => void;
  onRemove: (index: number) => void;
  onClear: () => void;
  testid: string;
}) {
  const { setting, files, problem, ceilings, onFiles, onRemove, onClear, testid } = props;
  const pickRef = useRef<HTMLInputElement>(null);
  const multiple = setting.multiple === true;
  const staged = files.length > 0;
  const totalBytes = files.reduce((sum, file) => sum + file.size, 0);
  const extensions = setting.accept ? ` (${setting.accept.split(",").join(" ")})` : "";

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
      <FileDropzone testId={`${testid}-dropzone`} multiple={multiple} onFiles={onFiles}>
        {!staged ? (
          <>
            Drop {setting.label.toLowerCase()} here{extensions}
            {multiple ? ", several at once if you have them" : ""}
          </>
        ) : multiple ? (
          <span data-testid={`${testid}-staged`}>
            {files.length} {files.length === 1 ? "file" : "files"} staged - drop more to add them
          </span>
        ) : (
          <span data-testid={`${testid}-staged`}>
            <span className="text-fg">{files[0].name}</span>{" "}
            <span className="text-fg-faint">({formatBytes(files[0].size)})</span> - drop another to
            replace it
          </span>
        )}
      </FileDropzone>

      {multiple && staged && (
        <>
          <ul className="mt-2 space-y-1 text-[12px]" data-testid={`${testid}-staged-list`}>
            {files.map((file, index) => (
              <li
                key={file.name}
                className="flex items-center gap-2"
                data-testid={`${testid}-staged-file`}
              >
                {/* The position, because it is not decoration: a provider composing its files gives
                    a re-declared path to the first one that declares it. */}
                <span className="text-fg-faint">{index + 1}.</span>
                <span className="text-fg min-w-0 truncate">{file.name}</span>
                <span className="text-fg-faint">{formatBytes(file.size)}</span>
                <button
                  type="button"
                  className="btn ml-auto"
                  data-testid={`${testid}-remove-${index}`}
                  onClick={() => onRemove(index)}
                >
                  remove
                </button>
              </li>
            ))}
          </ul>
          {/* The TOTAL, because it is a refusal of its own: the runtime bounds one job's files
              together as well as each file on its own. */}
          <span className="text-fg-faint block text-[11px]" data-testid={`${testid}-total`}>
            {files.length} {files.length === 1 ? "file" : "files"}, {formatBytes(totalBytes)} in
            total
          </span>
        </>
      )}

      <div className="mt-2 flex items-center gap-2">
        <button
          type="button"
          className="btn"
          data-testid={`${testid}-pick`}
          onClick={() => pickRef.current?.click()}
        >
          {multiple ? (staged ? "add files" : "pick files") : staged ? "replace" : "pick a file"}
        </button>
        {/* For a set, only once there is more than one to clear: with a single file the row's own
            remove is already that button. */}
        {(multiple ? files.length > 1 : staged) && (
          <button type="button" className="btn" data-testid={`${testid}-clear`} onClick={onClear}>
            {multiple ? "remove all" : "remove"}
          </button>
        )}
      </div>

      <input
        ref={pickRef}
        type="file"
        className="hidden"
        multiple={multiple}
        accept={setting.accept ?? undefined}
        aria-label={setting.label}
        data-testid={testid}
        onChange={(event) => {
          const picked = event.target.files ? Array.from(event.target.files) : [];
          // Cleared so that re-picking the same file fires a change again, which is what somebody
          // does after saving an edit to it.
          event.target.value = "";
          if (picked.length > 0) onFiles(picked);
        }}
      />

      {problem && (
        <span className="text-warn block text-[11px]" data-testid={`${testid}-problem`}>
          {problem}
        </span>
      )}

      <span className="label-help">
        {setting.help}{" "}
        {multiple && (
          <>
            The set of files is the source, taken together, so a later run with fewer of them
            withdraws whatever only the missing file described. Where two of them declare the same
            thing, the one listed first wins.{" "}
          </>
        )}
        {/* Deliberately NOT restating the descriptor's own help, which already says the file
            travels with the run and is dropped after it. Only what this screen knows: where it is
            kept, and when it is read. */}
        This tab keeps it for a re-run until you change it or pick another integration, and reads it
        when the run is SENT - so one you move or edit in the meantime fails then.
      </span>

      {/* The ceilings up front, not only inside a refusal. They are the INSTANCE's numbers, read
          from it, because a number kept here is how this form came to refuse jobs the instance
          would have accepted. */}
      <span className="text-fg-faint block text-[11px]" data-testid={`${testid}-ceilings`}>
        {ceilings}
      </span>
    </div>
  );
}

/** The counts, the failure kind when there is one, and every diagnostic with its own code. */
/**
 * A run while it happens: which phase, how far through it, and how long it has been going.
 *
 * The phase list is rendered from RUN_PHASES rather than from what the run has reported, so an
 * operator can see what is still to come and not only what has passed - which is the difference
 * between "it is on step 3 of 7" and "it said something once". Two of these phases can run for a long
 * time while the graph shows no change at all (a large extract parsing, and summary embedding), and
 * those are exactly the ones that used to be indistinguishable from a hang.
 *
 * Those same two phases are why a stop is offered here and why it is rendered as PENDING rather than
 * as taken: the run honours it at its next safe point, which for embedding is after the chunk already
 * in the model. What a stopped run leaves behind is stated once on the runtime's own report.
 */
function RunPanel({
  run,
  instance,
  onCancelAnswered,
}: {
  run: IntegrationRunState;
  instance: InstanceConfig;
  /** The 202's body, or null when the answer said nothing about the run (a 404, or a failed call). */
  onCancelAnswered: (answered: IntegrationRunState | null) => void;
}) {
  const done = new Set(run.completedPhases);
  const elapsed = formatElapsed(run.elapsedMilliseconds);
  // Two-step in place, like the other destructive actions here, and the first step is deliberately
  // not the request: a run costs hours and there is no way to resume one that was stopped.
  const [armed, setArmed] = useState(false);
  const stopping = run.running && run.cancelRequested === true;

  const cancel = useMutation({
    mutationFn: () => cancelIntegrationRun(instance, run.integrationInstanceId),
    onSuccess: (answered) => {
      setArmed(false);
      onCancelAnswered(answered);
    },
    onError: () => {
      // Refreshed on failure too, because the interesting failure is the 404: the run ended between
      // the last poll and the click, so it is this panel's view that is stale rather than anything
      // being wrong.
      setArmed(false);
      onCancelAnswered(null);
    },
  });
  const alreadyEnded = cancel.error instanceof ApiError && cancel.error.status === 404;

  return (
    <section className="panel" data-testid="run-panel">
      <div className="panel-title">
        run - {run.running ? "in flight" : run.cancelled ? "cancelled" : "finished"}
        <span className="text-fg-faint normal-case" data-testid="run-elapsed">
          {run.integrationInstanceId} · {elapsed}
        </span>
      </div>
      {/* Shown for a finished resumed run as well as one still going: its report's counts cover
          only the part after the pickup, so without this line a run that matched everything reads
          as one that did almost nothing. */}
      {run.resumed && (
        <p className="text-fg-dim px-3 pb-2 text-[11px]" data-testid="run-resumed">
          Picked up after a restart of the integrations runtime, continuing where it stopped. It is
          the same run, so the elapsed time above includes the outage.
        </p>
      )}
      <ul className="space-y-1 px-3 pb-3 text-[12px]">
        {RUN_PHASES.map((phase) => {
          const isCurrent = run.phase === phase;
          const isDone = done.has(phase);
          const state = isCurrent ? "running" : isDone ? "done" : "pending";
          return (
            <li
              key={phase}
              className="flex items-center gap-2"
              data-testid={`run-phase-${phase}`}
              data-state={state}
            >
              <span
                className={
                  isCurrent ? "text-accent" : isDone ? "text-fg" : "text-fg-faint"
                }
              >
                {isDone ? "✓" : isCurrent ? "▸" : "·"}
              </span>
              <span className={isCurrent ? "text-accent" : isDone ? "text-fg" : "text-fg-faint"}>
                {phase}
              </span>
              {isCurrent && run.phaseTotal > 0 && (
                <span className="text-fg-faint" data-testid={`run-count-${phase}`}>
                  {run.phaseDone.toLocaleString()} / {run.phaseTotal.toLocaleString()}
                </span>
              )}
            </li>
          );
        })}
      </ul>
      {run.running && (
        <p className="text-fg-faint px-3 pb-3 text-[11px]">
          This run continues on the server whether or not this page is open, and closing the browser
          does not stop it. Come back to this screen and it re-attaches.
        </p>
      )}
      {run.running && (
        <div className="space-y-1 px-3 pb-3">
          <div className="flex items-center gap-2">
            {armed && !stopping ? (
              <>
                <button
                  type="button"
                  className="btn btn-danger"
                  data-testid="integration-run-cancel-confirm"
                  disabled={cancel.isPending}
                  onClick={() => cancel.mutate()}
                >
                  {cancel.isPending ? "asking..." : "yes, stop it"}
                </button>
                <button
                  type="button"
                  className="btn"
                  data-testid="integration-run-cancel-keep"
                  onClick={() => setArmed(false)}
                >
                  keep it running
                </button>
                <span className="text-fg-faint text-[11px]">
                  It keeps everything it has already written.
                </span>
              </>
            ) : (
              <button
                type="button"
                className="btn btn-danger"
                data-testid="integration-run-cancel"
                disabled={stopping || cancel.isPending}
                onClick={() => setArmed(true)}
              >
                {stopping ? "cancelling..." : "stop this run"}
              </button>
            )}
          </div>
          {stopping && (
            <p className="text-warn text-[11px]" data-testid="run-cancelling">
              Asked to stop, waiting for the run's next safe point. In embed-summaries that is after
              the chunk of 16 already in the model, which on CPU inference is a wait and not an
              instant.
            </p>
          )}
        </div>
      )}
      {!run.running && run.cancelled && (
        <p className="text-fg-dim px-3 pb-3 text-[11px]" data-testid="run-cancelled">
          Stopped on request{run.stoppedInPhase ? ` in ${run.stoppedInPhase}` : ""}. Nothing was
          withdrawn or deleted: it kept what it had written and deliberately did not reconcile, so
          the next completed run under this identity converges the graph.
        </p>
      )}
      {!run.running && !run.cancelled && run.cancelRequested && (
        <p className="text-fg-faint px-3 pb-3 text-[11px]" data-testid="run-cancel-too-late">
          The run completed. The stop arrived after its last safe point, so it finished normally and
          this is not a cancelled run.
        </p>
      )}
      {cancel.isError &&
        (alreadyEnded ? (
          <p className="text-fg-dim px-3 pb-3 text-[11px]" data-testid="run-cancel-already-ended">
            That run had already ended, so there was nothing to stop. A finished run is deliberately
            not cancellable, and what this one ended as is above.
          </p>
        ) : (
          <div className="px-3 pb-3" data-testid="run-cancel-error">
            <ErrorBox error={cancel.error} />
          </div>
        ))}
      {run.error && (
        <p className="text-warn px-3 pb-3 text-[11px]" data-testid="run-error">
          The run ended without producing a report: {run.error}
        </p>
      )}
    </section>
  );
}

/** Elapsed time as a person reads it. Hours matter here: an embedding phase runs for them. */
function formatElapsed(milliseconds: number): string {
  const total = Math.max(0, Math.floor(milliseconds / 1000));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const seconds = total % 60;
  if (hours > 0) return `${hours}h ${minutes.toString().padStart(2, "0")}m`;
  if (minutes > 0) return `${minutes}m ${seconds.toString().padStart(2, "0")}s`;
  return `${seconds}s`;
}

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
        {/* Without this a cancelled report reads exactly like a successful one: it carries no
            errorKind, because being stopped is not a failure. What it means for the graph is on the
            run panel above; this says only that the counts are partial. */}
        {report.cancelled && (
          <p className="text-fg-dim text-[12px]" data-testid="integration-report-cancelled">
            cancelled: the counts below are what really landed before the run stopped, not a failure
            and not a whole import.
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

/**
 * One file staged in this tab, waiting for a run: its name, its size and the browser's HANDLE.
 *
 * Not its bytes. Reading them here is what made staging a vehicle's extracts leave gigabytes resident before
 * the send started, and the base64 the send then needed capped a job at about 384 MiB whatever the
 * instance would have accepted. A handle costs nothing until the request streams it off disk.
 */
type StagedFile = { name: string; size: number; file: File };

/**
 * Reads ONE BYTE, to find out whether the file can still be read at all.
 *
 * The file's contents are not read here any more: a job streams from the handle at send time
 * (feature integration-file-transport). But a handle can go stale - the file gets moved, renamed or
 * replaced between the picker and the button - and without a probe that failure would surface
 * minutes into a send instead of at the pick. One byte is enough: the browser opens the file to
 * serve it, which is the thing being tested.
 *
 * FileReader rather than `Blob.slice().arrayBuffer()`, because jsdom does not implement the latter:
 * it typechecks against the DOM lib and then throws at test time.
 */
function probeReadable(file: File): Promise<void> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(reader.error ?? new Error("the file could not be read"));
    reader.onload = () => resolve();
    reader.readAsArrayBuffer(file.slice(0, 1));
  });
}

/**
 * What the run button says, which is four things rather than two.
 *
 * "starting" was the whole of it when a job was a small JSON body, and it is a lie for a multipart
 * send of several gigabytes: the operator who prompted this feature pressed run, saw one unchanging
 * word for minutes, and reasonably concluded nothing was happening. So the label distinguishes
 * sending from waiting, and names the share sent while it is sending.
 *
 * The last stage is deliberately "starting the run", not "running": this call ends when the runtime
 * ACCEPTS the job, and the run itself outlives it and is watched in its own panel.
 */
function sendLabel(pending: boolean, progress: UploadProgress | null): string {
  if (!pending) return "run now";
  if (progress === null) return "starting…";
  if (progress.total !== null && progress.total > 0) {
    if (progress.sent >= progress.total) return "starting the run…";
    return `sending ${Math.min(99, Math.floor((progress.sent / progress.total) * 100))}%…`;
  }
  return progress.sent > 0 ? "sending…" : "starting…";
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
  files: Record<string, StagedFile[]>,
): string[] {
  return provider.settings
    .filter((setting) =>
      setting.required &&
      (setting.kind === "File"
        ? (files[setting.key]?.length ?? 0) === 0
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
  staged: Record<string, StagedFile[]>,
  embedSummaries: boolean,
  embeddingName: string,
): IntegrationJobRequest {
  const settings: Record<string, string> = {};
  const credentialValues: Record<string, string> = {};
  const files: Record<string, IntegrationJobFile | IntegrationJobFile[]> = {};

  for (const setting of provider.settings) {
    if (setting.kind === "File") {
      // The HANDLE, not the bytes: nothing reads the file until the request streams it. The name is
      // NOT trimmed, because the file's own name is what every message about the run will call it.
      // The staged order is kept, because a provider composing its files reads it as precedence.
      const carried = (staged[setting.key] ?? []).map((staged) => ({
        name: staged.name,
        file: staged.file,
      }));

      if (carried.length > 0) {
        // The SHAPE comes from the descriptor and never from the count: the runtime refuses a list
        // for a setting that takes one file, so a set of one still travels as the bare object unless
        // the setting was declared multiple.
        files[setting.key] = setting.multiple === true ? carried : carried[0];
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
