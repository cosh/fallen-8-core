// MIT License
//
// NlAssistPanel.tsx
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

import { useEffect, useRef, useState } from "react";
import type { DelegateKind, DelegateValidationResult } from "../../api/types";
import { help } from "../../lib/fieldHelp";
import {
  effectiveNlConfig,
  isLoopbackEndpoint,
  isNlConfigured,
  useNlAssist,
  withNlAssistPolicyGate,
} from "./config";
import { NlBackendConfig } from "./NlBackendConfig";
import { downloadText, toTrainingJsonl, type TrainingExample, type Verdict } from "./feedback";
import { NlDraftList, type NlDraftView } from "./NlDraftList";
import { formatFragment } from "./format";
import { buildGenerationPrompt, buildRefinePrompt, extractFragment } from "./prompt";
import {
  generateChat,
  initialMessages,
  probeEndpoint,
  type ChatTurn,
  type NlGenerationStats,
} from "./generate";
import { useActiveInstance } from "../../instances/registry";

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
  /** FL-2 feedback capture: the user's 👍/👎 on this draft (null until rated). */
  verdict: Verdict | null;
  /** Capture time, for the exported training example. */
  ts: number;
}

interface NlAssistPanelProps {
  delegateKind: DelegateKind;
  currentFragment: string;
  onDraft: (code: string) => void;
  validateDraft: (code: string) => Promise<DelegateValidationResult | null>;
  drivingRef: React.MutableRefObject<boolean>;
}

export const NlAssistPanel = withNlAssistPolicyGate(NlAssistPanelInner);

function NlAssistPanelInner({
  delegateKind,
  currentFragment,
  onDraft,
  validateDraft,
  drivingRef,
}: NlAssistPanelProps) {
  // The store is already policy-resolved: the persist merge in ./config.ts applies
  // resolveNlConfig at rehydrate, so an instance-only embed never holds a custom config.
  const { config, leaveNoticeAccepted, setConfig, acceptLeaveNotice } = useNlAssist();
  const instance = useActiveInstance();
  const [intent, setIntent] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const [attempts, setAttempts] = useState<NlAttempt[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [showConfig, setShowConfig] = useState(false);
  const [reachable, setReachable] = useState<boolean | null>(null);
  const abortRef = useRef<AbortController | null>(null);

  const isInstance = config.mode === "instance";
  const effective = effectiveNlConfig(config);
  const configured = isInstance ? instance !== null : isNlConfigured(config);
  // Instance mode targets the already-trusted instance, so it never shows an egress notice.
  const needsLeaveNotice =
    !isInstance && configured && !isLoopbackEndpoint(effective.endpoint) && !leaveNoticeAccepted;

  // Informational reachability probe (FR-2) - never gates generation. Custom mode only:
  // instance mode's reachability is the instance connection itself (shown on Connect).
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
    // Deps are the effective backend's primitives - `effective` itself is a new object
    // every render and would re-probe in a loop.
  }, [configured, isInstance, effective.endpoint, effective.apiKind, effective.model, effective.apiKey]);

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
      for (let attempt = 0; attempt <= config.maxRetries; attempt++) {
        setBusy(attempt === 0 ? "generating…" : `refining (${attempt}/${config.maxRetries})…`);
        const { content, stats } = await generateChat(
          config,
          instance,
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
          { fragment: draft, intent, valid: result?.valid ?? null, errorCount, stats, verdict: null, ts: Date.now() },
        ]);

        if (result === null || result.valid) break;
        if (attempt === config.maxRetries) break;

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

  // FL-2 feedback capture (opt-in, local): rate a draft, then export the rated ones as a
  // training-example JSONL. No network - the file is the operator's to move (parent privacy rule).
  const rateAttempt = (index: number, verdict: Verdict) =>
    setAttempts((previous) =>
      previous.map((attempt, i) =>
        i === index ? { ...attempt, verdict: attempt.verdict === verdict ? null : verdict } : attempt,
      ),
    );

  const ratedAttempts = attempts.filter((attempt) => attempt.verdict !== null);

  const exportTrainingExamples = () => {
    const examples: TrainingExample[] = ratedAttempts.map((attempt) => ({
      delegateKind,
      intent: attempt.intent,
      fragment: attempt.fragment,
      verdict: attempt.verdict,
      ts: attempt.ts,
    }));
    downloadText(`f8-training-${delegateKind}-${Date.now()}.jsonl`, toTrainingJsonl(examples));
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
            {isInstance ? (
              <>
                this instance · {instance?.name ?? "?"} → <code>/chat</code> (server-selected
                model)
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
              title={help("nlIntent")}
              data-testid="nl-intent"
              className="input h-32 resize-none"
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
              <NlDraftList
                testid="nl-attempts"
                verdictTestidPrefix="nl-verdict"
                drafts={attempts.map(
                  (attempt): NlDraftView => ({
                    valid: attempt.valid,
                    verdict: attempt.verdict,
                    active: attempt.fragment === currentFragment,
                    loadTitle: attempt.fragment,
                    labelSuffix:
                      attempt.valid === false ? ` (${attempt.errorCount} error(s))` : undefined,
                    below: attempt.stats ? (
                      <>
                        <div className="text-fg-faint pl-4 text-[10px]">
                          {statsLine(attempt.stats)}
                        </div>
                        <details className="text-fg-faint pl-4 text-[10px]">
                          <summary className="cursor-pointer">raw stats</summary>
                          <pre className="overflow-x-auto whitespace-pre-wrap">
                            {JSON.stringify(attempt.stats.raw, null, 1)}
                          </pre>
                        </details>
                      </>
                    ) : undefined,
                  }),
                )}
                onLoad={(index) => onDraft(attempts[index].fragment)}
                onRate={rateAttempt}
              />
            )}
            {ratedAttempts.length > 0 && (
              <button
                type="button"
                data-testid="nl-export-training"
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
