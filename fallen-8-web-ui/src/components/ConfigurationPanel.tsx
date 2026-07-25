import { useState } from "react";
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

function gpuLabel(gpu: boolean | null | undefined): string | null {
  if (gpu === true) return "GPU";
  if (gpu === false) return "CPU";
  return null; // unknown / not reported → show nothing
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
          <Row label="loaded" value={embedding.loaded ? "yes" : "not yet"} />
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
  const gpu = gpuLabel(chat?.gpu);
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
          <Row label="loaded" value={chat.loaded ? "yes" : "not yet"} />
          {gpu && <Row label="device" value={gpu} />}
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
            restart — this view is read-only.
          </Dialog.Description>
          <div className="mt-3" data-testid="config-observability-overlay">
            <EnvRow
              label="OTLP endpoint"
              value={observability.otlpEnabled ? (observability.otlpEndpoint ?? "(set)") : "off"}
              envKey="Fallen8__Observability__Otlp__Endpoint"
            />
            <EnvRow
              label="Prometheus /metrics"
              value={observability.prometheusEnabled ? "on" : "off"}
              envKey="Fallen8__Observability__Prometheus__Enabled"
            />
            <EnvRow
              label="/metrics needs key"
              value={observability.prometheusRequireApiKey ? "yes" : "no"}
              envKey="Fallen8__Observability__Prometheus__RequireApiKey"
            />
            <EnvRow
              label="trace sampling"
              value={observability.tracingSamplingRatio.toString()}
              envKey="Fallen8__Observability__TracingSamplingRatio"
            />
            <EnvRow
              label="statistics budget"
              value={observability.statisticsElementBudget.toLocaleString()}
              envKey="Fallen8__Observability__StatisticsElementBudget"
            />
            <EnvRow
              label="statistics top-N"
              value={observability.statisticsTopN.toString()}
              envKey="Fallen8__Observability__StatisticsTopN"
            />
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
