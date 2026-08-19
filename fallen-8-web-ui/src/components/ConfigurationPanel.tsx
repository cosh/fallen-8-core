// MIT License
//
// ConfigurationPanel.tsx
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

import { useState, type ReactNode } from "react";
import * as Dialog from "@radix-ui/react-dialog";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useActiveInstance } from "../instances/registry";
import { usePortalContainer, useStudioConfig } from "../app/studioConfig";
import { useConfig } from "../state/status";
import { writeConfig } from "../api/endpoints";
import type { ApiError } from "../api/client";
import type {
  ChatProviderStatsREST,
  EmbeddingProviderStatsREST,
  ObservabilityConfigREST,
} from "../api/types";
import { Truncated } from "./Truncated";
import { SettingRow } from "./SettingRow";
import { restartBannerSummary } from "../lib/restartCopy";
import { ErrorBox } from "./ErrorBox";

/**
 * Connect · Configuration (features instance-config and writable-instance-config): the
 * instance-scoped configuration home, between Instances and Namespaces. It lists every setting this
 * instance binds with its tier, effective value and source, lets an operator edit the writable ones,
 * and shows the semantic providers plus the observability posture with a details overlay.
 *
 * Most settings only take effect at the next boot, and the panel says so per row rather than implying
 * a restart is never needed. It is the codebase's first dirty-state form, which is why the config poll
 * is suspended while there are unsaved edits.
 *
 * The browser's NL routing preference does NOT live here, despite what this docstring claimed for a
 * while: it is a per-browser choice and has its own home.
 */

/**
 * The two keys that decide the namespace startup policy. They are instance-wide settings, so they sit
 * in this panel, but an embed that locked namespace management must not be able to re-plan the host's
 * next boot through them either, so they take the namespace lock on top of the instance lock.
 */
function isNamespacePolicy(key: string, lockNamespace?: boolean): boolean {
  return (
    lockNamespace === true &&
    (key === "Fallen8:Namespaces:LoadOnStartup" || key === "Fallen8:Namespaces:StartupLoadMode")
  );
}
function Row({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex items-baseline gap-2 text-[12px]">
      <span className="text-fg-faint w-20 shrink-0 tracking-wide uppercase">{label}</span>
      <span className="text-fg-dim min-w-0 truncate" title={value}>
        {value}
      </span>
    </div>
  );
}

type ModelStatus = { text: string; state: "loaded" | "idle" | "unknown" };

/**
 * The provider's live status. Prefers the model-residency probe (Ollama /api/ps) when known —
 * that is the honest "is the model loaded right now" signal — and falls back to the lazy
 * `loaded` flag (client created on first use) for non-Ollama backends or when the probe is
 * inconclusive. "idle" is normal: a provider loads its model on first use.
 */
function modelStatus(
  loaded: boolean,
  resident: boolean | null | undefined,
  gpu: boolean | null | undefined,
): ModelStatus {
  if (resident === true) {
    const device = gpu === true ? " · GPU" : gpu === false ? " · CPU" : "";
    return { text: `loaded${device}`, state: "loaded" };
  }
  if (resident === false) return { text: "not loaded (loads on first use)", state: "idle" };
  return loaded
    ? { text: "loaded", state: "loaded" }
    : { text: "idle (loads on first use)", state: "idle" };
}

function StatusRow({ status }: { status: ModelStatus }) {
  const dot =
    status.state === "loaded" ? "bg-accent" : status.state === "idle" ? "bg-fg-faint" : "bg-warn";
  return (
    <div className="flex items-center gap-2 text-[12px]" data-testid="config-model-status">
      <span className="text-fg-faint w-20 shrink-0 tracking-wide uppercase">status</span>
      <span className={`inline-block h-1.5 w-1.5 shrink-0 rounded-full ${dot}`} aria-hidden />
      <span className="text-fg-dim min-w-0 truncate">{status.text}</span>
    </div>
  );
}

function EmbeddingCard({ embedding }: { embedding: EmbeddingProviderStatsREST | null | undefined }) {
  return (
    <div className="border-line rounded border p-3" data-testid="config-embedding">
      <div className="flex items-baseline gap-2">
        <span className="text-fg font-bold">Embedding</span>
        <StateBadge on={embedding?.enabled === true} />
      </div>
      {embedding?.enabled ? (
        <div className="mt-2 space-y-0.5">
          <Row label="backend" value={embedding.backend ?? "—"} />
          <Row
            label="model"
            value={
              embedding.modelName
                ? embedding.modelName + (embedding.modelVersion ? `@${embedding.modelVersion}` : "")
                : "—"
            }
          />
          <Row label="vector" value={`${embedding.dimension}d · ${embedding.intendedMetric ?? "—"}`} />
          <StatusRow status={modelStatus(embedding.loaded, embedding.resident, embedding.gpu)} />
        </div>
      ) : (
        <p className="text-fg-faint mt-2 text-[11px]">
          Off — text-in embedding and semantic search answer 403; bring-your-own-vector paths work.
        </p>
      )}
    </div>
  );
}

function ChatCard({ chat }: { chat: ChatProviderStatsREST | null | undefined }) {
  return (
    <div className="border-line rounded border p-3" data-testid="config-chat">
      <div className="flex items-baseline gap-2">
        <span className="text-fg font-bold">Chat / language model</span>
        <StateBadge on={chat?.enabled === true} />
      </div>
      {chat?.enabled ? (
        <div className="mt-2 space-y-0.5">
          <Row label="backend" value={chat.backend ?? "—"} />
          <Row label="model" value={chat.model ?? "—"} />
          <StatusRow status={modelStatus(chat.loaded, chat.resident, chat.gpu)} />
        </div>
      ) : (
        <p className="text-fg-faint mt-2 text-[11px]">
          Off — POST /chat answers 403. Enable it via the docker environment (F8_CHAT) or the
          Fallen8:Chat config section.
        </p>
      )}
    </div>
  );
}

function StateBadge({ on }: { on: boolean }) {
  return (
    <span
      className={`ml-auto rounded border px-1.5 py-0.5 text-[10px] tracking-wider uppercase ${
        on ? "border-accent/40 text-accent" : "border-line text-fg-faint"
      }`}
    >
      {on ? "on" : "off"}
    </span>
  );
}

function observabilitySummary(o: ObservabilityConfigREST): string {
  const parts: string[] = [];
  if (o.otlpEnabled) parts.push(`pushing metrics + traces + logs to ${o.otlpEndpoint ?? "OTLP"}`);
  if (o.prometheusEnabled) parts.push("Prometheus scrape at /metrics");
  return parts.length > 0 ? parts.join(" · ") : "Off — no exporter configured";
}

export function ConfigurationPanel() {
  const instance = useActiveInstance();
  const [showObservability, setShowObservability] = useState(false);
  // The draft holds only the rows the operator touched. A key mapped to null is a pending CLEAR.
  const [draft, setDraft] = useState<Record<string, string | null>>({});
  const [writeError, setWriteError] = useState<ApiError | Error | null>(null);
  const { lockInstances, lockNamespace } = useStudioConfig();
  const queryClient = useQueryClient();
  const dirty = Object.keys(draft).length > 0;
  // Every hook must sit above the !instance guard below, so the fabricated instance is passed here too.
  const activeOrPlaceholder =
    instance ?? ({ id: "none", name: "", baseUrl: "", auth: { kind: "none" } } as never);
  // Suspended while dirty: a ten second refetch would otherwise replace a value under a half-typed
  // field. It exists for model residency, which is never worth losing someone's input over.
  const config = useConfig(activeOrPlaceholder, { poll: !dirty });

  const write = useMutation({
    mutationFn: (settings: Record<string, string | null>) =>
      writeConfig(activeOrPlaceholder, { settings }),
    onSuccess: () => {
      setDraft({});
      setWriteError(null);
      // Nothing else refreshes the read surface: window-focus refetch is off globally and the poll is
      // still suspended at the moment this returns.
      void queryClient.invalidateQueries({ queryKey: [activeOrPlaceholder.id, "config"] });
    },
    onError: (error: ApiError | Error) => setWriteError(error),
  });

  if (!instance) return null;

  const settings = config.data?.settings ?? [];
  const pendingRestart = config.data?.pendingRestart ?? [];
  // A write needs an API key configured server-side, so without one every row is read-only and the
  // panel says why rather than offering a Save that would always be refused.
  const writesAllowed = config.data?.apiKeyRequired === true;
  const editable = !lockInstances;

  return (
    <section className="panel" data-testid="configuration-panel">
      <div className="panel-title">
        Configuration
        <span className="text-fg-faint normal-case">this instance</span>
        {dirty && (
          <span className="text-warn normal-case" data-testid="config-dirty">
            unsaved changes
          </span>
        )}
        <button
          type="button"
          className="btn ml-auto flex shrink-0 items-center gap-1.5 normal-case"
          data-testid="config-refresh"
          title={
            dirty
              ? "Discard the unsaved changes and reload what this instance currently reports"
              : "Re-check the providers (model residency is probed live; the panel also re-checks every 10s while there is nothing unsaved)"
          }
          disabled={config.isFetching || write.isPending}
          onClick={() => {
            setDraft({});
            setWriteError(null);
            void config.refetch();
          }}
        >
          {config.isFetching && (
            <span
              className="inline-block h-3 w-3 animate-spin rounded-full border border-current border-t-transparent"
              aria-hidden
            />
          )}
          {config.isFetching ? "checking…" : dirty ? "Discard" : "Refresh"}
        </button>
      </div>
      <div className="space-y-4 p-3">
        {config.isPending && <div className="text-fg-faint text-[12px]">checking…</div>}
        {config.isError && (
          <div className="text-fg-faint text-[12px]" data-testid="config-unavailable">
            Configuration unavailable — the instance is unreachable, or an API key is required
            (set it above).
          </div>
        )}

        {config.data && (
          <>
            <div>
              <div className="text-fg-faint mb-2 text-[10px] tracking-widest uppercase">
                semantic providers
              </div>
              <div className="grid gap-3 md:grid-cols-2">
                <EmbeddingCard embedding={config.data.semantic?.embedding} />
                <ChatCard chat={config.data.semantic?.chat} />
              </div>
            </div>

            {pendingRestart.length > 0 && (
              <div
                className="border-warn/50 text-warn rounded border p-2 text-[11px]"
                data-testid="config-pending-restart"
              >
                <div className="font-medium">{restartBannerSummary(pendingRestart.length)}</div>
                <ul className="text-fg-dim mt-1 space-y-0.5">
                  {pendingRestart.map((entry) => (
                    <li key={entry.key}>
                      <code className="text-[10px]">{entry.key}</code>: running{" "}
                      <span className="text-fg">{entry.runningValue ?? "unset"}</span>, pending{" "}
                      <span className="text-fg">{entry.pendingValue ?? "unset"}</span>
                    </li>
                  ))}
                </ul>
              </div>
            )}

            {settings.length > 0 && (
              <div>
                <div className="text-fg-faint mb-2 flex items-center gap-2 text-[10px] tracking-widest uppercase">
                  settings
                  <span className="normal-case tracking-normal">
                    {writesAllowed ? "" : "(read-only: configuring an API key is what allows a write)"}
                  </span>
                  {editable && dirty && (
                    <button
                      type="button"
                      className="btn-accent btn ml-auto normal-case"
                      data-testid="config-save"
                      disabled={write.isPending}
                      onClick={() => write.mutate(draft)}
                    >
                      {write.isPending ? "saving…" : `Save ${Object.keys(draft).length}`}
                    </button>
                  )}
                </div>

                {writeError && (
                  <div className="mb-2" data-testid="config-settings-error">
                    <ErrorBox error={writeError} />
                  </div>
                )}

                <div className="scroll-list">
                  {settings.map((setting) => (
                    <SettingRow
                      key={setting.key}
                      setting={setting}
                      draft={draft[setting.key]}
                      disabled={
                        !editable || !writesAllowed || isNamespacePolicy(setting.key, lockNamespace)
                      }
                      onChange={(value) => setDraft((current) => ({ ...current, [setting.key]: value }))}
                      onClear={() => setDraft((current) => ({ ...current, [setting.key]: null }))}
                    />
                  ))}
                </div>
              </div>
            )}

            <div>
              <div className="text-fg-faint mb-2 text-[10px] tracking-widest uppercase">
                observability
              </div>
              <div className="flex items-center gap-2">
                <span className="text-fg-dim min-w-0 text-[12px]" data-testid="config-observability-summary">
                  <Truncated text={observabilitySummary(config.data.observability)} max={80} />
                </span>
                <button
                  type="button"
                  className="btn ml-auto shrink-0"
                  data-testid="config-observability-configure"
                  onClick={() => setShowObservability(true)}
                >
                  Configure…
                </button>
              </div>
            </div>
          </>
        )}
      </div>

      {config.data && (
        <ObservabilityOverlay
          open={showObservability}
          observability={config.data.observability}
          onClose={() => setShowObservability(false)}
        />
      )}
    </section>
  );
}

function EnvRow({ label, value, envKey }: { label: string; value: string; envKey: string }) {
  // Every row gets a stable handle derived from its label (the shape DelegateSlot.tsx already uses),
  // so a docs capture can assert that the row it photographs is actually configured without this
  // component growing one prop per screenshot. "OTLP endpoint" -> config-otlp-endpoint.
  const testId = `config-${label.replace(/[^a-z0-9]+/gi, "-").toLowerCase()}`;
  return (
    <div className="border-line grid grid-cols-[10rem_1fr] items-baseline gap-2 border-b py-1.5 text-[12px] last:border-b-0">
      <span className="text-fg-dim">{label}</span>
      <div className="min-w-0">
        <div className="text-fg wrap-break-word" data-testid={testId}>
          {value}
        </div>
        <code className="text-fg-faint text-[10px]">{envKey}</code>
      </div>
    </div>
  );
}

function ObsSection({ title, hint, children }: { title: string; hint: string; children: ReactNode }) {
  return (
    <div>
      <div className="text-accent text-[10px] font-bold tracking-widest uppercase">{title}</div>
      <p className="text-fg-faint mt-0.5 mb-1 text-[11px]">{hint}</p>
      <div>{children}</div>
    </div>
  );
}

function ObservabilityOverlay({
  open,
  observability,
  onClose,
}: {
  open: boolean;
  observability: ObservabilityConfigREST;
  onClose: () => void;
}) {
  const portalContainer = usePortalContainer();
  return (
    <Dialog.Root open={open} onOpenChange={(o) => !o && onClose()}>
      <Dialog.Portal container={portalContainer}>
        <Dialog.Overlay className="modal-overlay" />
        <Dialog.Content className="panel modal-center flex max-h-[90vh] w-[34rem] max-w-[92vw] flex-col p-4">
          <Dialog.Title className="text-fg text-sm font-bold">Observability</Dialog.Title>
          <Dialog.Description className="text-fg-dim mt-1 text-[12px]">
            What this instance is exporting right now. These particular values are read-only: the
            exporter switches and the endpoint decide the security posture of a metrics surface, so
            they are set where the instance is deployed. The statistics bounds beside them ARE editable,
            in the Settings list above.
          </Dialog.Description>
          <div
            className="mt-3 min-h-0 flex-1 space-y-4 overflow-y-auto pr-1"
            data-testid="config-observability-overlay"
          >
            <ObsSection
              title="Push (OTLP)"
              hint="Metrics, traces, and logs pushed to a collector. This is the live path in the default environment."
            >
              <EnvRow
                label="OTLP endpoint"
                value={observability.otlpEnabled ? (observability.otlpEndpoint ?? "(set)") : "off"}
                envKey="Fallen8__Observability__Otlp__Endpoint"
              />
              <EnvRow
                label="trace sampling"
                value={observability.tracingSamplingRatio.toString()}
                envKey="Fallen8__Observability__TracingSamplingRatio"
              />
            </ObsSection>

            <ObsSection
              title="Pull (Prometheus scrape)"
              hint="An optional GET /metrics endpoint a Prometheus server scrapes. Off by default and independent of the push above; leave it off when pushing."
            >
              <EnvRow
                label="scrape endpoint"
                value={observability.prometheusEnabled ? "on (GET /metrics)" : "off"}
                envKey="Fallen8__Observability__Prometheus__Enabled"
              />
              <EnvRow
                label="requires API key"
                value={observability.prometheusRequireApiKey ? "yes" : "no"}
                envKey="Fallen8__Observability__Prometheus__RequireApiKey"
              />
            </ObsSection>

            <ObsSection
              title="Statistics snapshot"
              hint="Bounds for the on-demand GET /statistics graph-shape snapshot. Not an exporter."
            >
              <EnvRow
                label="element budget"
                value={observability.statisticsElementBudget.toLocaleString()}
                envKey="Fallen8__Observability__StatisticsElementBudget"
              />
              <EnvRow
                label="top-N"
                value={observability.statisticsTopN.toString()}
                envKey="Fallen8__Observability__StatisticsTopN"
              />
            </ObsSection>
          </div>
          <div className="mt-4 flex justify-end">
            <button type="button" className="btn" onClick={onClose}>
              Close
            </button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
