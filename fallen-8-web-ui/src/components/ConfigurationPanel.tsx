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

import { useCallback, useEffect, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useActiveInstance } from "../instances/registry";
import { useStudioConfig } from "../app/studioConfig";
import { useConfig } from "../state/status";
import { writeConfig } from "../api/endpoints";
import type { ApiError } from "../api/client";
import type {
  ChatProviderStatsREST,
  EmbeddingProviderStatsREST,
  ObservabilityConfigREST,
} from "../api/types";
import { Truncated } from "./Truncated";
import { ConfigurationSurface } from "./ConfigurationSurface";
import { restartBannerSummary } from "../lib/restartCopy";
import { ErrorBox } from "./ErrorBox";

/**
 * Connect · Configuration (features instance-config, writable-instance-config and
 * configuration-surface): the instance-scoped configuration home, between Instances and Namespaces.
 *
 * This is the SUMMARY: the semantic providers with their live model residency, whether anything is
 * waiting for a restart, what the instance is exporting, and one Configure button. The settings
 * themselves live behind that button, in ConfigurationSurface. They used to be inline here as one flat
 * list of every catalogued key, which is what made the Connect screen unreadable.
 *
 * The card keeps owning the server state, because it outlives the dialog: the draft, the write
 * mutation, the poll suspension and the lock gating are all here. Most settings only take effect at the
 * next boot, and the surface says so per row rather than implying a restart is never needed.
 *
 * The browser's NL routing preference does NOT live here, despite what this docstring claimed for a
 * while: it is a per-browser choice and has its own home.
 */

/**
 * Namespace-policy keys are instance-wide settings, so they sit behind this card, but an embed that
 * locked namespace management must not be able to re-plan the host's namespaces through them either,
 * so they take the namespace lock on top of the instance lock. A prefix rule rather than a key list:
 * the server encodes the grouping in the key itself, and a list would silently miss the next key
 * added under the section.
 */
function isNamespacePolicy(key: string, lockNamespace?: boolean): boolean {
  return lockNamespace === true && key.startsWith("Fallen8:Namespaces:");
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
  const [showSettings, setShowSettings] = useState(false);
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
  // field. It exists for model residency, which is never worth losing someone's input over. The
  // suspension has to hold with the surface CLOSED too, because closing it keeps the draft.
  const config = useConfig(activeOrPlaceholder, { poll: !dirty });

  const write = useMutation({
    mutationFn: (settings: Record<string, string | null>) =>
      writeConfig(activeOrPlaceholder, { settings }),
    onSuccess: (_result, written) => {
      // Remove only the keys this save carried: edits typed into OTHER rows while the request was in
      // flight are someone's unsaved work, and wiping them would silently discard it.
      setDraft((current) =>
        Object.fromEntries(Object.entries(current).filter(([key]) => !(key in written))),
      );
      setWriteError(null);
      // Nothing else refreshes the read surface: window-focus refetch is off globally and the poll is
      // still suspended at the moment this returns.
      void queryClient.invalidateQueries({ queryKey: [activeOrPlaceholder.id, "config"] });
    },
    onError: (error: ApiError | Error) => setWriteError(error),
  });

  // The draft belongs to the instance it was typed against. Switching the active instance must drop
  // it, or Save would write one instance's intended values into another's configuration. The surface
  // closes with it: leaving it open would show one instance's rows while the other's are refetched.
  const instanceId = instance?.id;
  useEffect(() => {
    setDraft({});
    setWriteError(null);
    setShowSettings(false);
  }, [instanceId]);

  const onRowChange = useCallback((key: string, value: string) => {
    setDraft((current) => ({ ...current, [key]: value }));
  }, []);
  const onRowClear = useCallback((key: string) => {
    setDraft((current) => ({ ...current, [key]: null }));
  }, []);

  if (!instance) return null;

  const settings = config.data?.settings ?? [];
  const pendingRestart = config.data?.pendingRestart ?? [];
  // The server says whether it accepts a write at all (both operator acts in place). Gating on the
  // API key alone would render an editor whose every Save is refused on a keyed instance that has
  // not enabled the capability; absent on an older server means no write route exists, so read-only.
  const writesAllowed = config.data?.configWriteEnabled === true;
  const editable = !lockInstances;

  // A blanked numeric field is neither a value nor a clear, and the server refuses the WHOLE batch
  // over it; Save waits until the field says something.
  const kinds = new Map(settings.map((setting) => [setting.key, setting.kind]));
  const blankNumericKey = Object.entries(draft).find(
    ([key, value]) => value === "" && (kinds.get(key) === "int" || kinds.get(key) === "double"),
  )?.[0];

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
                {/* The count only. Which keys, running against pending, is the surface's disclosure:
                    it is a list, and a list is what this card exists not to be. */}
                {restartBannerSummary(pendingRestart.length)}
              </div>
            )}

            {/* A failed write is reported here as well as in the surface, because someone can close the
                surface on a refusal and the card must not look like the save went through. */}
            {writeError && !showSettings && (
              <div data-testid="config-settings-error">
                <ErrorBox error={writeError} />
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
              </div>
            </div>

            <div className="border-line flex items-center gap-2 border-t pt-3">
              <span className="text-fg-faint min-w-0 text-[11px]" data-testid="config-settings-summary">
                {settings.length === 0
                  ? "This instance publishes no settings inventory."
                  : `${settings.length} setting${settings.length === 1 ? "" : "s"}, ${
                      settings.filter((s) => s.source === "override").length
                    } set here`}
              </span>
              <button
                type="button"
                className="btn ml-auto shrink-0"
                data-testid="config-configure"
                onClick={() => setShowSettings(true)}
              >
                Configure…
              </button>
            </div>
          </>
        )}
      </div>

      {config.data && (
        <ConfigurationSurface
          open={showSettings}
          onClose={() => setShowSettings(false)}
          instanceName={instance.name}
          settings={settings}
          pendingRestart={pendingRestart}
          observability={config.data.observability}
          draft={draft}
          dirtyCount={Object.keys(draft).length}
          onChange={onRowChange}
          onClear={onRowClear}
          onSave={() => write.mutate(draft)}
          saving={write.isPending}
          writeError={writeError}
          writesAllowed={writesAllowed}
          editable={editable}
          isRowDisabled={isRowDisabled}
          blankNumericKey={blankNumericKey}
        />
      )}
    </section>
  );

  function isRowDisabled(key: string): boolean {
    return !editable || !writesAllowed || isNamespacePolicy(key, lockNamespace);
  }
}
