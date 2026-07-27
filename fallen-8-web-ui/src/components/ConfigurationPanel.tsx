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
import { useActiveInstance } from "../instances/registry";
import { useConfig } from "../state/status";
import type {
  ChatProviderStatsREST,
  EmbeddingProviderStatsREST,
  ObservabilityConfigREST,
} from "../api/types";
import { Truncated } from "./Truncated";

/**
 * Connect · Configuration (feature instance-config): the instance-scoped, read-only config
 * home, between Instances and Namespaces. It shows the semantic providers (embedding + chat
 * gateway, sourced from GET /config) and the observability posture ("pushing to <endpoint>"),
 * with a details overlay. Server config is startup-bound, so this is display + guidance, not
 * an editor. The browser NL routing preference lives here too (added by the NL-assist reroute).
 */
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
  const config = useConfig(instance ?? ({ id: "none", name: "", baseUrl: "", auth: { kind: "none" } } as never));

  if (!instance) return null;

  return (
    <section className="panel" data-testid="configuration-panel">
      <div className="panel-title">
        Configuration
        <span className="text-fg-faint normal-case">this instance · read-only</span>
        <button
          type="button"
          className="btn ml-auto flex shrink-0 items-center gap-1.5 normal-case"
          data-testid="config-refresh"
          title="Re-check the providers (model residency is probed live; the panel also re-checks every 10s)"
          disabled={config.isFetching}
          onClick={() => void config.refetch()}
        >
          {config.isFetching && (
            <span
              className="inline-block h-3 w-3 animate-spin rounded-full border border-current border-t-transparent"
              aria-hidden
            />
          )}
          {config.isFetching ? "checking…" : "Refresh"}
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
  return (
    <div className="border-line grid grid-cols-[10rem_1fr] items-baseline gap-2 border-b py-1.5 text-[12px] last:border-b-0">
      <span className="text-fg-dim">{label}</span>
      <div className="min-w-0">
        <div className="text-fg wrap-break-word">{value}</div>
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
  return (
    <Dialog.Root open={open} onOpenChange={(o) => !o && onClose()}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-black/60" />
        <Dialog.Content className="panel fixed top-1/2 left-1/2 w-[34rem] max-w-[92vw] -translate-x-1/2 -translate-y-1/2 p-4">
          <Dialog.Title className="text-fg text-sm font-bold">Observability</Dialog.Title>
          <Dialog.Description className="text-fg-dim mt-1 text-[12px]">
            Set at startup via environment variables (or appsettings). Changes take effect on
            restart; this view is read-only.
          </Dialog.Description>
          <div className="mt-3 space-y-4" data-testid="config-observability-overlay">
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
