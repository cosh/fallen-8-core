import { useEffect, useRef, useState } from "react";
import type { DelegateKind, DelegateValidationResult } from "../../api/types";
import {
  BUILTIN_NL_BACKEND,
  effectiveNlConfig,
  isLoopbackEndpoint,
  isNlConfigured,
  NL_PRESETS,
  useNlAssist,
  usesApiKey,
} from "./config";
import { formatFragment } from "./format";
import { buildGenerationPrompt, buildRefinePrompt, extractFragment } from "./prompt";
import {
  chatWithModel,
  initialMessages,
  probeEndpoint,
  type ChatTurn,
  type NlGenerationStats,
} from "./generate";

/**
 * NL assist (FR-26, nl-assist + nl-assist-ux specs): builtin-by-default model backend,
 * generation grounded in the §6.1/§6.2 contract, bounded validate-and-refine loop,
 * never auto-submitted. Drafts accumulate as a clickable history with per-call stats.
 */

interface NlAttempt {
  fragment: string;
  intent: string;
  valid: boolean | null;
  errorCount: number;
  stats: NlGenerationStats | null;
}

export function NlAssistPanel({
  delegateKind,
  currentFragment,
  onDraft,
  validateDraft,
  drivingRef,
}: {
  delegateKind: DelegateKind;
  currentFragment: string;
  onDraft: (code: string) => void;
  validateDraft: (code: string) => Promise<DelegateValidationResult | null>;
  drivingRef: React.MutableRefObject<boolean>;
}) {
  const { config, leaveNoticeAccepted, setConfig, acceptLeaveNotice } = useNlAssist();
  const [intent, setIntent] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const [attempts, setAttempts] = useState<NlAttempt[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [showConfig, setShowConfig] = useState(false);
  const [reachable, setReachable] = useState<boolean | null>(null);
  const abortRef = useRef<AbortController | null>(null);

  const effective = effectiveNlConfig(config);
  const configured = isNlConfigured(config);
  const needsLeaveNotice =
    configured && !isLoopbackEndpoint(effective.endpoint) && !leaveNoticeAccepted;

  // Informational reachability probe (FR-2) - never gates generation.
  useEffect(() => {
    if (!configured) {
      setReachable(null);
      return;
    }
    const controller = new AbortController();
    setReachable(null);
    void probeEndpoint(effective, controller.signal).then((ok) => {
      if (!controller.signal.aborted) setReachable(ok);
    });
    return () => controller.abort();
    // Deps are the effective backend's primitives - `effective` itself is a new object
    // every render and would re-probe in a loop.
  }, [configured, effective.endpoint, effective.apiKind, effective.model, effective.apiKey]);

  const generate = async () => {
    setError(null);
    const controller = new AbortController();
    abortRef.current = controller;
    // Own validation for the duration of the loop so the editor's debounce cannot abort
    // our in-flight /delegates/validate calls (nl-assist spec FR-26.7).
    drivingRef.current = true;

    // Re-drafting the same intent asks for a distinct variant (FR-8).
    const priorDrafts = attempts
      .filter((attempt) => attempt.intent === intent)
      .map((attempt) => attempt.fragment);
    const prompt = buildGenerationPrompt(delegateKind, intent, priorDrafts);
    const conversation: ChatTurn[] = initialMessages(prompt);

    try {
      for (let attempt = 0; attempt <= effective.maxRetries; attempt++) {
        setBusy(attempt === 0 ? "generating…" : `refining (${attempt}/${effective.maxRetries})…`);
        const { content, stats } = await chatWithModel(
          effective,
          conversation,
          controller.signal,
        );
        // Model output arrives as one line; pretty-print before it hits the editor.
        // Validation runs on the formatted text, so diagnostics match what's shown.
        const draft = formatFragment(extractFragment(content));

        // Insert as ordinary editable text (never auto-submit), then gate through the
        // same validation the user's own code goes through (FR-26.6/26.7).
        onDraft(draft);
        const result = await validateDraft(draft);
        const errorCount =
          result?.diagnostics.filter((d) => d.severity === "error").length ?? 0;
        // History accumulates across runs (FR-6).
        setAttempts((previous) => [
          ...previous,
          { fragment: draft, intent, valid: result?.valid ?? null, errorCount, stats },
        ]);

        if (result === null || result.valid) break;
        if (attempt === effective.maxRetries) break;

        conversation.push({ role: "assistant", content });
        conversation.push({
          role: "user",
          content: buildRefinePrompt(delegateKind, draft, result.diagnostics),
        });
      }
    } catch (e) {
      if (!controller.signal.aborted) {
        setError(e instanceof Error ? e.message : String(e));
      }
    } finally {
      drivingRef.current = false;
      setBusy(null);
    }
  };

  return (
    <div className="border-line border-t">
      <div className="panel-title">
        nl assist
        <button
          type="button"
          className="text-fg-faint hover:text-fg ml-auto cursor-pointer normal-case"
          onClick={() => setShowConfig((s) => !s)}
        >
          {showConfig ? "close" : "configure"}
        </button>
      </div>
      <div className="space-y-2 p-2 text-[12px]">
        {configured && (
          <p className="text-fg-faint text-[10px]" data-testid="nl-backend-status">
            {config.mode === "builtin" ? "built-in" : "custom"} · {effective.endpoint} ·{" "}
            {effective.model} —{" "}
            {reachable === null ? "checking…" : reachable ? "reachable" : "not reachable"}
            {reachable === false && config.mode === "builtin" && (
              <>
                {" "}
                (start it: <code>docker compose up ollama</code>, or install Ollama,{" "}
                <code>ollama pull phi4-mini</code> and set <code>OLLAMA_ORIGINS</code> to
                this origin)
              </>
            )}
          </p>
        )}

        {showConfig && (
          <div className="space-y-2" data-testid="nl-config">
            <div>
              <label className="label" htmlFor="nl-mode">
                backend
              </label>
              <select
                id="nl-mode"
                className="input w-auto"
                value={config.mode}
                onChange={(e) => setConfig({ mode: e.target.value as "builtin" | "custom" })}
              >
                <option value="builtin">built-in (local Ollama)</option>
                <option value="custom">custom</option>
              </select>
            </div>
            {config.mode === "builtin" ? (
              <p className="text-fg-faint text-[10px]" data-testid="nl-builtin-hint">
                Fixed to the stack this project ships in docker-compose.yml:{" "}
                <code>{BUILTIN_NL_BACKEND.endpoint}</code> · ollama ·{" "}
                <code>{BUILTIN_NL_BACKEND.model}</code> (MIT weights + MIT runtime).
                Nothing to configure.
              </p>
            ) : (
              <>
                <div>
                  <label className="label" htmlFor="nl-preset">
                    preset
                  </label>
                  <select
                    id="nl-preset"
                    className="input w-auto"
                    value=""
                    onChange={(e) => {
                      const preset = NL_PRESETS.find((p) => p.name === e.target.value);
                      if (preset) {
                        setConfig({
                          endpoint: preset.endpoint,
                          apiKind: preset.apiKind,
                          model: preset.model,
                        });
                      }
                    }}
                  >
                    <option value="">— prefill from preset —</option>
                    {NL_PRESETS.map((preset) => (
                      <option key={preset.name} value={preset.name}>
                        {preset.name}
                      </option>
                    ))}
                  </select>
                </div>
                <div>
                  <label className="label" htmlFor="nl-endpoint">
                    endpoint
                  </label>
                  <input
                    id="nl-endpoint"
                    className="input"
                    value={config.endpoint}
                    onChange={(e) => setConfig({ endpoint: e.target.value })}
                    placeholder="http://localhost:11434"
                  />
                </div>
                <div className="flex gap-2">
                  <div>
                    <label className="label" htmlFor="nl-kind">
                      api
                    </label>
                    <select
                      id="nl-kind"
                      className="input w-auto"
                      value={config.apiKind}
                      onChange={(e) =>
                        setConfig({ apiKind: e.target.value as "ollama" | "openai" })
                      }
                    >
                      <option value="ollama">ollama</option>
                      <option value="openai">openai-compatible</option>
                    </select>
                  </div>
                  <div className="grow">
                    <label className="label" htmlFor="nl-model">
                      model
                    </label>
                    <input
                      id="nl-model"
                      className="input"
                      value={config.model}
                      onChange={(e) => setConfig({ model: e.target.value })}
                      placeholder="phi4-mini"
                    />
                  </div>
                  <div className="w-16">
                    <label className="label" htmlFor="nl-temperature">
                      temp
                    </label>
                    <input
                      id="nl-temperature"
                      className="input"
                      type="number"
                      min="0"
                      max="2"
                      step="0.1"
                      value={config.temperature}
                      onChange={(e) =>
                        setConfig({ temperature: Number(e.target.value) || 0 })
                      }
                    />
                  </div>
                </div>
                {usesApiKey(config) ? (
                  <div>
                    <label className="label" htmlFor="nl-key">
                      api key (optional — sent only to the model endpoint)
                    </label>
                    <input
                      id="nl-key"
                      className="input"
                      type="password"
                      value={config.apiKey ?? ""}
                      onChange={(e) => setConfig({ apiKey: e.target.value || undefined })}
                    />
                  </div>
                ) : (
                  <p className="text-fg-faint text-[10px]" data-testid="nl-no-key-hint">
                    No API key — Ollama endpoints never use one.
                  </p>
                )}
                <p className="text-fg-faint text-[10px]">
                  Presets are prefills, not recommendations — the blessed setup stays the
                  built-in MIT stack. Hosted endpoints must send CORS headers and show a
                  “text leaves this machine” notice; for your own Ollama set{" "}
                  <code>OLLAMA_ORIGINS</code> to this app's origin.
                </p>
              </>
            )}
          </div>
        )}

        {!configured && !showConfig && (
          <p className="text-fg-faint" data-testid="nl-disabled-hint">
            No model backend configured — the editor is fully usable without it. Under
            “configure” switch back to the built-in backend or set a custom endpoint.
          </p>
        )}

        {configured && (
          <>
            {needsLeaveNotice && (
              <div
                className="border-warn/50 text-warn rounded border p-2"
                data-testid="nl-leave-notice"
              >
                The endpoint <code>{effective.endpoint}</code> is not local: your
                description and the included type-surface context will leave this machine.
                <button
                  type="button"
                  className="btn mt-1 block"
                  onClick={() => acceptLeaveNotice()}
                >
                  Understood
                </button>
              </div>
            )}
            <textarea
              aria-label="describe the filter"
              data-testid="nl-intent"
              className="input h-16 resize-none"
              value={intent}
              onChange={(e) => setIntent(e.target.value)}
              placeholder='e.g. "only persons older than 30"'
            />
            <div className="flex items-center gap-2">
              <button
                type="button"
                className="btn btn-accent"
                data-testid="nl-generate"
                disabled={!intent.trim() || busy !== null || needsLeaveNotice}
                onClick={() => void generate()}
              >
                {busy ?? "Draft fragment"}
              </button>
              {busy && (
                <button
                  type="button"
                  className="btn"
                  onClick={() => abortRef.current?.abort()}
                >
                  Cancel
                </button>
              )}
              {attempts.length > 0 && !busy && (
                <button
                  type="button"
                  className="text-fg-faint hover:text-fg ml-auto cursor-pointer text-[10px]"
                  data-testid="nl-clear-attempts"
                  onClick={() => setAttempts([])}
                >
                  clear
                </button>
              )}
            </div>
            {attempts.length > 0 && (
              <ol className="space-y-1" data-testid="nl-attempts">
                {attempts.map((attempt, index) => (
                  <li key={index}>
                    <div className="flex items-center gap-1">
                      <span
                        className={
                          attempt.valid
                            ? "text-accent"
                            : attempt.valid === false
                              ? "text-danger"
                              : "text-fg-faint"
                        }
                      >
                        {attempt.valid ? "✓" : attempt.valid === false ? "✗" : "?"}
                      </span>
                      <button
                        type="button"
                        className={`cursor-pointer truncate hover:underline ${
                          attempt.fragment === currentFragment
                            ? "text-fg font-semibold"
                            : "text-accent-2"
                        }`}
                        title={attempt.fragment}
                        onClick={() => onDraft(attempt.fragment)}
                      >
                        draft {index + 1}
                        {attempt.fragment === currentFragment && " (in editor)"}
                        {attempt.valid === false && ` (${attempt.errorCount} error(s))`}
                      </button>
                      {attempt.stats && (
                        <span className="text-fg-faint ml-auto shrink-0 text-[10px]">
                          {statsLine(attempt.stats)}
                        </span>
                      )}
                    </div>
                    {attempt.stats && (
                      <details className="text-fg-faint pl-4 text-[10px]">
                        <summary className="cursor-pointer">raw stats</summary>
                        <pre className="overflow-x-auto whitespace-pre-wrap">
                          {JSON.stringify(attempt.stats.raw, null, 1)}
                        </pre>
                      </details>
                    )}
                  </li>
                ))}
              </ol>
            )}
            {error && <div className="text-danger">{error}</div>}
          </>
        )}
      </div>
    </div>
  );
}

function statsLine(stats: NlGenerationStats): string {
  const parts: string[] = [];
  if (stats.promptTokens !== undefined || stats.completionTokens !== undefined) {
    parts.push(`${stats.promptTokens ?? "?"}→${stats.completionTokens ?? "?"} tok`);
  }
  if (stats.durationMs !== undefined) parts.push(`${(stats.durationMs / 1000).toFixed(1)}s`);
  if (stats.tokensPerSecond !== undefined)
    parts.push(`${stats.tokensPerSecond.toFixed(1)} tok/s`);
  return parts.join(" · ");
}
