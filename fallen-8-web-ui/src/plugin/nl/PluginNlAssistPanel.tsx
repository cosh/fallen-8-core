import { useEffect, useRef, useState } from "react";
import type {
  AlgorithmContract,
  PluginAuthoringCategory,
  PluginValidationResult,
} from "../../api/types";
import { help } from "../../lib/fieldHelp";
import {
  effectiveNlConfig,
  isLoopbackEndpoint,
  isNlConfigured,
  useNlAssist,
} from "../../delegate/nl/config";
import { NlBackendConfig } from "../../delegate/nl/NlBackendConfig";
import { downloadText, toTrainingJsonl, type Verdict } from "../../delegate/nl/feedback";
import {
  generateChat,
  initialMessages,
  probeEndpoint,
  type ChatTurn,
  type NlGenerationStats,
} from "../../delegate/nl/generate";
import { useActiveInstance } from "../../instances/registry";
import { buildPluginGenerationPrompt, buildPluginRefinePrompt, extractType } from "./pluginPrompt";

/**
 * NL assist for WHOLE-TYPE plugin authoring (feature plugin-registration §6): the same
 * builtin-by-default backend, bounded validate-and-refine loop, never-auto-submitted,
 * clickable draft history as the delegate fragment editor — but it drafts a complete plugin
 * type and validates it through the plugin compile-check (POST /plugins/{category}/validate,
 * threaded in as `validateDraft`). The model backend config + transport + training export are
 * the shared delegate-NL infra (src/delegate/nl/); only the prompt/extract are whole-type.
 */

interface PluginNlAttempt {
  source: string;
  intent: string;
  valid: boolean | null;
  error: string | null;
  stats: NlGenerationStats | null;
  verdict: Verdict | null;
  ts: number;
}

export function PluginNlAssistPanel({
  category,
  contract,
  name,
  scaffold,
  currentSource,
  onDraft,
  validateDraft,
  drivingRef,
}: {
  category: PluginAuthoringCategory;
  contract: AlgorithmContract;
  name: string;
  scaffold: string;
  currentSource: string;
  onDraft: (source: string) => void;
  validateDraft: (source: string) => Promise<PluginValidationResult | null>;
  drivingRef: React.MutableRefObject<boolean>;
}) {
  const { config, leaveNoticeAccepted, setConfig, acceptLeaveNotice } = useNlAssist();
  const instance = useActiveInstance();
  const [intent, setIntent] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const [attempts, setAttempts] = useState<PluginNlAttempt[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [showConfig, setShowConfig] = useState(false);
  const [reachable, setReachable] = useState<boolean | null>(null);
  const abortRef = useRef<AbortController | null>(null);

  const isInstance = config.mode === "instance";
  const effective = effectiveNlConfig(config);
  const configured = isInstance ? instance !== null : isNlConfigured(config);
  const needsLeaveNotice =
    !isInstance && configured && !isLoopbackEndpoint(effective.endpoint) && !leaveNoticeAccepted;

  useEffect(() => {
    if (!configured || isInstance) {
      setReachable(null);
      return;
    }
    const controller = new AbortController();
    setReachable(null);
    void probeEndpoint(effective, controller.signal).then((ok) => {
      if (!controller.signal.aborted) setReachable(ok);
    });
    return () => controller.abort();
  }, [configured, isInstance, effective.endpoint, effective.apiKind, effective.model, effective.apiKey]);

  const generate = async () => {
    setError(null);
    const controller = new AbortController();
    abortRef.current = controller;
    // Own validation for the loop so the editor's debounce cannot abort our in-flight checks.
    drivingRef.current = true;

    const priorDrafts = attempts.filter((a) => a.intent === intent).map((a) => a.source);
    const prompt = buildPluginGenerationPrompt({ category, contract, name, scaffold, intent, priorDrafts });
    const conversation: ChatTurn[] = initialMessages(prompt);

    try {
      for (let attempt = 0; attempt <= config.maxRetries; attempt++) {
        setBusy(attempt === 0 ? "generating…" : `refining (${attempt}/${config.maxRetries})…`);
        const { content, stats } = await generateChat(config, instance, conversation, controller.signal);
        const draft = extractType(content);

        // Insert as ordinary editable text (never auto-submit), then gate through the same
        // compile-check the author's own source goes through.
        onDraft(draft);
        const result = await validateDraft(draft);
        setAttempts((previous) => [
          ...previous,
          {
            source: draft,
            intent,
            valid: result?.valid ?? null,
            error: result?.error ?? null,
            stats,
            verdict: null,
            ts: Date.now(),
          },
        ]);

        if (result === null || result.valid) break;
        if (attempt === config.maxRetries) break;

        conversation.push({ role: "assistant", content });
        conversation.push({
          role: "user",
          content: buildPluginRefinePrompt({ category, contract, name, source: draft, error: result.error ?? "" }),
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

  const rateAttempt = (index: number, verdict: Verdict) =>
    setAttempts((previous) =>
      previous.map((a, i) => (i === index ? { ...a, verdict: a.verdict === verdict ? null : verdict } : a)),
    );

  const ratedAttempts = attempts.filter((a) => a.verdict !== null);

  const exportTrainingExamples = () => {
    const examples = ratedAttempts.map((a) => ({
      kind: "plugin" as const,
      category,
      contract: category === "algorithm" ? contract : undefined,
      name,
      intent: a.intent,
      source: a.source,
      verdict: a.verdict,
      ts: a.ts,
    }));
    downloadText(`f8-training-plugin-${category}-${Date.now()}.jsonl`, toTrainingJsonl(examples));
  };

  return (
    <div className="border-line flex min-h-0 flex-col border-l">
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
      <div className="min-h-0 space-y-2 overflow-auto p-2 text-[12px]">
        {configured && (
          <p className="text-fg-faint text-[10px]" data-testid="plugin-nl-backend-status">
            {isInstance ? (
              <>
                this instance · {instance?.name ?? "?"} → <code>/chat</code> (server-selected model)
              </>
            ) : (
              <>
                custom · {effective.endpoint} · {effective.model} —{" "}
                {reachable === null ? "checking…" : reachable ? "reachable" : "not reachable"}
              </>
            )}
          </p>
        )}

        {showConfig && <NlBackendConfig config={config} setConfig={setConfig} />}

        {!configured && !showConfig && (
          <p className="text-fg-faint" data-testid="plugin-nl-disabled-hint">
            No model backend configured — authoring is fully usable without it. Under “configure”
            switch back to the built-in backend or set a custom endpoint.
          </p>
        )}

        {configured && (
          <>
            {needsLeaveNotice && (
              <div className="border-warn/50 text-warn rounded border p-2" data-testid="plugin-nl-leave-notice">
                The endpoint <code>{effective.endpoint}</code> is not local: your description and the
                plugin scaffold context will leave this machine.
                <button type="button" className="btn mt-1 block" onClick={() => acceptLeaveNotice()}>
                  Understood
                </button>
              </div>
            )}
            <textarea
              aria-label="describe the plugin"
              title={help("pluginParameters")}
              data-testid="plugin-nl-intent"
              className="input h-16 resize-none"
              value={intent}
              onChange={(e) => setIntent(e.target.value)}
              placeholder={
                category === "function"
                  ? 'e.g. "all vertices labelled person and the edges between them"'
                  : 'e.g. "breadth-first shortest path ignoring edges labelled blocked"'
              }
            />
            <div className="flex items-center gap-2">
              <button
                type="button"
                className="btn btn-accent"
                data-testid="plugin-nl-generate"
                disabled={!intent.trim() || busy !== null || needsLeaveNotice}
                onClick={() => void generate()}
              >
                {busy ?? "Draft plugin"}
              </button>
              {busy && (
                <button type="button" className="btn" onClick={() => abortRef.current?.abort()}>
                  Cancel
                </button>
              )}
              {attempts.length > 0 && !busy && (
                <button
                  type="button"
                  className="text-fg-faint hover:text-fg ml-auto cursor-pointer text-[10px]"
                  data-testid="plugin-nl-clear-attempts"
                  onClick={() => setAttempts([])}
                >
                  clear
                </button>
              )}
            </div>
            {attempts.length > 0 && (
              <ol className="space-y-1" data-testid="plugin-nl-attempts">
                {attempts.map((attempt, index) => (
                  <li key={index}>
                    <div className="flex items-center gap-1">
                      <span
                        className={
                          attempt.valid ? "text-accent" : attempt.valid === false ? "text-danger" : "text-fg-faint"
                        }
                      >
                        {attempt.valid ? "✓" : attempt.valid === false ? "✗" : "?"}
                      </span>
                      <button
                        type="button"
                        className={`cursor-pointer truncate hover:underline ${
                          attempt.source === currentSource ? "text-fg font-semibold" : "text-accent-2"
                        }`}
                        title="load this draft into the editor"
                        onClick={() => onDraft(attempt.source)}
                      >
                        draft {index + 1}
                        {attempt.source === currentSource && " (in editor)"}
                        {attempt.valid === false && " (invalid)"}
                      </button>
                      <span className="ml-auto flex shrink-0 gap-1" data-testid={`plugin-nl-verdict-${index}`}>
                        <button
                          type="button"
                          title="good draft — mark to save as a training example"
                          className={`cursor-pointer ${attempt.verdict === "up" ? "text-accent" : "text-fg-faint hover:text-fg"}`}
                          onClick={() => rateAttempt(index, "up")}
                        >
                          👍
                        </button>
                        <button
                          type="button"
                          title="bad draft — mark to save as a training example"
                          className={`cursor-pointer ${attempt.verdict === "down" ? "text-danger" : "text-fg-faint hover:text-fg"}`}
                          onClick={() => rateAttempt(index, "down")}
                        >
                          👎
                        </button>
                      </span>
                    </div>
                  </li>
                ))}
              </ol>
            )}
            {ratedAttempts.length > 0 && (
              <button
                type="button"
                data-testid="plugin-nl-export-training"
                className="text-accent-2 cursor-pointer text-[11px] hover:underline"
                title="Download the rated drafts as a training-example file (stays on this machine)"
                onClick={exportTrainingExamples}
              >
                save {ratedAttempts.length} training example{ratedAttempts.length === 1 ? "" : "s"}
              </button>
            )}
            {error && <div className="text-danger">{error}</div>}
          </>
        )}
      </div>
    </div>
  );
}
