import { useCallback, useEffect, useRef, useState } from "react";
import * as Dialog from "@radix-ui/react-dialog";
import Editor, { type OnMount } from "@monaco-editor/react";
import type * as Monaco from "monaco-editor";
import { setupMonaco } from "./monacoSetup";
import { diagnosticsToMarkers } from "./markers";
import { KIND_INFO } from "./kinds";
import { registerDelegateProviders } from "./providers";
import { snippetCodeFor, snippetsForKind } from "./snippets";
import { validateDelegate } from "../api/endpoints";
import { ApiError } from "../api/client";
import type { DelegateDiagnostic, DelegateKind, DelegateValidationResult } from "../api/types";
import type { InstanceConfig } from "../instances/types";
import {
  isLoopbackEndpoint,
  isNlConfigured,
  useNlAssist,
  usesApiKey,
} from "./nl/config";
import { buildGenerationPrompt, buildRefinePrompt, extractFragment } from "./nl/prompt";
import { chatWithModel, initialMessages, type ChatTurn } from "./nl/generate";

setupMonaco();

/**
 * The shared delegate editor (FR-22..26): one component for every fragment slot, opened
 * as a modal. Diagnostics come exclusively from POST /delegates/validate and are rendered
 * at the returned positions - the server already mapped them to fragment coordinates, so
 * this file MUST NOT map them again (FR-24).
 */

type ValidationState =
  | { phase: "idle" }
  | { phase: "validating" }
  | { phase: "done"; fragment: string; result: DelegateValidationResult }
  | { phase: "gate"; status: number; message: string }
  | { phase: "error"; message: string };

interface NlAttempt {
  fragment: string;
  valid: boolean | null;
  errorCount: number;
}

export function DelegateEditor({
  instance,
  delegateKind,
  contextLabel,
  initialFragment,
  onCommit,
  onCancel,
}: {
  instance: InstanceConfig;
  delegateKind: DelegateKind;
  contextLabel: string;
  initialFragment: string;
  onCommit: (fragment: string) => void;
  onCancel: () => void;
}) {
  const info = KIND_INFO[delegateKind];
  const [fragment, setFragment] = useState(
    initialFragment.trim() === "" ? info.openingSnippet : initialFragment,
  );
  const [validation, setValidation] = useState<ValidationState>({ phase: "idle" });
  const editorRef = useRef<Monaco.editor.IStandaloneCodeEditor | null>(null);
  const monacoRef = useRef<typeof Monaco | null>(null);
  const disposeProviders = useRef<(() => void) | null>(null);
  const abortRef = useRef<AbortController | null>(null);
  // While the NL loop drives validation itself, the debounce must not fire and abort its
  // in-flight /delegates/validate call (that would break the refine loop after one turn).
  const nlDrivingRef = useRef(false);

  const isEmpty = fragment.trim() === "" || fragment.trim() === info.openingSnippet.trim();
  // Commit is allowed only when the CURRENT fragment text is the one that passed
  // validation (FR-25): editing after a VALID result re-blocks commit until the new text
  // is validated, so unvalidated code can never reach the query endpoints.
  const canCommit =
    isEmpty ||
    (validation.phase === "done" &&
      validation.result.valid &&
      validation.fragment === fragment);

  const applyMarkers = useCallback((diagnostics: DelegateDiagnostic[]) => {
    const monaco = monacoRef.current;
    const model = editorRef.current?.getModel();
    if (!monaco || !model) return;
    // Positions arrive in fragment coordinates - rendered verbatim (FR-24).
    monaco.editor.setModelMarkers(model, "f8-validate", diagnosticsToMarkers(diagnostics));
  }, []);

  const runValidation = useCallback(
    async (code: string): Promise<DelegateValidationResult | null> => {
      abortRef.current?.abort();
      const controller = new AbortController();
      abortRef.current = controller;
      setValidation({ phase: "validating" });
      try {
        const result = await validateDelegate(instance, delegateKind, code, controller.signal);
        if (controller.signal.aborted) return null;
        const final = result ?? { valid: false, diagnostics: [] };
        setValidation({ phase: "done", fragment: code, result: final });
        applyMarkers(final.diagnostics);
        return final;
      } catch (error) {
        if (controller.signal.aborted) return null;
        if (error instanceof ApiError && (error.status === 401 || error.status === 403)) {
          setValidation({
            phase: "gate",
            status: error.status,
            message:
              error.status === 403
                ? "Dynamic code execution is disabled on this instance (Fallen8:Security:EnableDynamicCodeExecution)."
                : "This instance requires an API key (configure it on the Connect screen).",
          });
        } else {
          setValidation({
            phase: "error",
            message: error instanceof Error ? error.message : String(error),
          });
        }
        return null;
      }
    },
    [instance, delegateKind, applyMarkers],
  );

  // Debounced validate-as-you-type (FR-23). Skipped while the NL loop is driving
  // validation directly, so it cannot abort the loop's in-flight request.
  useEffect(() => {
    if (nlDrivingRef.current) return;
    if (isEmpty) {
      setValidation({ phase: "idle" });
      applyMarkers([]);
      return;
    }
    const timer = window.setTimeout(() => void runValidation(fragment), 600);
    return () => window.clearTimeout(timer);
  }, [fragment, isEmpty, runValidation, applyMarkers]);

  const handleMount: OnMount = (editor, monaco) => {
    editorRef.current = editor;
    monacoRef.current = monaco;
    disposeProviders.current = registerDelegateProviders(monaco, delegateKind);
    // Cursor after the arrow of the opening snippet.
    const model = editor.getModel();
    if (model && fragment === info.openingSnippet) {
      const end = model.getFullModelRange().getEndPosition();
      editor.setPosition(end);
    }
    editor.focus();
  };

  useEffect(
    () => () => {
      disposeProviders.current?.();
      abortRef.current?.abort();
    },
    [],
  );

  const insertSnippet = (code: string) => {
    setFragment(code);
    editorRef.current?.setValue(code);
  };

  return (
    <Dialog.Root open onOpenChange={(open) => !open && onCancel()}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-black/60" />
        <Dialog.Content className="panel fixed top-1/2 left-1/2 z-50 flex h-[80vh] w-[64rem] max-w-[95vw] -translate-x-1/2 -translate-y-1/2 flex-col">
          <div className="panel-title">
            <Dialog.Title className="contents">
              delegate editor — {delegateKind}
            </Dialog.Title>
            <span className="text-fg-faint normal-case">{contextLabel}</span>
            <span className="text-fg-faint ml-auto normal-case">{info.lambdaShape}</span>
          </div>

          <div className="flex min-h-0 flex-1">
            <div className="flex min-w-0 flex-1 flex-col">
              <div className="min-h-0 flex-1">
                <Editor
                  language="csharp"
                  theme="vs-dark"
                  value={fragment}
                  onChange={(value) => setFragment(value ?? "")}
                  onMount={handleMount}
                  options={{
                    minimap: { enabled: false },
                    fontSize: 13,
                    fontFamily: "JetBrains Mono, monospace",
                    lineNumbers: "on",
                    scrollBeyondLastLine: false,
                    automaticLayout: true,
                    fixedOverflowWidgets: true,
                  }}
                />
              </div>

              <div className="border-line flex items-center gap-2 border-t px-3 py-2">
                <ValidationBadge state={validation} isEmpty={isEmpty} fragment={fragment} />
                <button
                  type="button"
                  className="btn ml-auto"
                  data-testid="validate-now"
                  disabled={isEmpty || validation.phase === "validating"}
                  onClick={() => void runValidation(fragment)}
                >
                  Validate
                </button>
                <button type="button" className="btn" onClick={onCancel}>
                  Cancel
                </button>
                <button
                  type="button"
                  className="btn btn-accent"
                  data-testid="commit-fragment"
                  disabled={!canCommit}
                  title={
                    canCommit
                      ? undefined
                      : "Blocked: the fragment must pass validation first (the query endpoints swallow compile errors)."
                  }
                  onClick={() => onCommit(isEmpty ? "" : fragment)}
                >
                  Use fragment
                </button>
              </div>
            </div>

            <aside className="border-line w-72 shrink-0 overflow-auto border-l">
              <div className="panel-title">snippets</div>
              <div className="space-y-1 p-2">
                {snippetsForKind(delegateKind).map((snippet) => (
                  <button
                    key={snippet.title}
                    type="button"
                    className="btn w-full justify-start text-left"
                    title={snippet.description}
                    onClick={() => insertSnippet(snippetCodeFor(snippet, info.parameterName))}
                  >
                    {snippet.title}
                  </button>
                ))}
                <p className="text-fg-faint px-1 pt-1 text-[10px]">
                  Empty fragment = match everything / no custom cost.
                </p>
              </div>
              <NlAssistPanel
                delegateKind={delegateKind}
                onDraft={(code) => insertSnippet(code)}
                validateDraft={runValidation}
                drivingRef={nlDrivingRef}
              />
            </aside>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

function ValidationBadge({
  state,
  isEmpty,
  fragment,
}: {
  state: ValidationState;
  isEmpty: boolean;
  fragment: string;
}) {
  if (isEmpty) {
    return <span className="text-fg-faint text-[11px]">empty = match everything</span>;
  }
  // A "done" result for text the user has since edited is stale - report not-validated
  // so the badge never claims VALID for uncommitted-safe text (matches the commit gate).
  if (state.phase === "done" && state.fragment !== fragment) {
    return <span className="text-fg-faint text-[11px]">edited — not validated</span>;
  }
  switch (state.phase) {
    case "idle":
      return <span className="text-fg-faint text-[11px]">not validated</span>;
    case "validating":
      return <span className="text-fg-dim text-[11px]">validating…</span>;
    case "done":
      return state.result.valid ? (
        <span className="text-accent text-[11px] font-semibold" data-testid="validation-valid">
          VALID
          {state.result.diagnostics.length > 0 &&
            ` (${state.result.diagnostics.length} warning(s))`}
        </span>
      ) : (
        <span className="text-danger text-[11px] font-semibold" data-testid="validation-invalid">
          INVALID — {state.result.diagnostics.filter((d) => d.severity === "error").length}{" "}
          error(s)
        </span>
      );
    case "gate":
      return (
        <span className="text-warn text-[11px]" data-testid="validation-gate">
          {state.message}
        </span>
      );
    case "error":
      return <span className="text-danger text-[11px]">validation failed: {state.message}</span>;
  }
}

/**
 * NL assist (FR-26, nl-assist spec): local-first model backend, generation grounded in
 * the §6.1/§6.2 contract, bounded validate-and-refine loop, never auto-submitted.
 */
function NlAssistPanel({
  delegateKind,
  onDraft,
  validateDraft,
  drivingRef,
}: {
  delegateKind: DelegateKind;
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
  const abortRef = useRef<AbortController | null>(null);

  const configured = isNlConfigured(config);
  const needsLeaveNotice =
    configured && !isLoopbackEndpoint(config.endpoint) && !leaveNoticeAccepted;

  const generate = async () => {
    setError(null);
    setAttempts([]);
    const controller = new AbortController();
    abortRef.current = controller;
    // Own validation for the duration of the loop so the editor's debounce cannot abort
    // our in-flight /delegates/validate calls (nl-assist spec FR-26.7).
    drivingRef.current = true;

    const prompt = buildGenerationPrompt(delegateKind, intent);
    const conversation: ChatTurn[] = initialMessages(prompt);

    try {
      for (let attempt = 0; attempt <= config.maxRetries; attempt++) {
        setBusy(attempt === 0 ? "generating…" : `refining (${attempt}/${config.maxRetries})…`);
        const raw = await chatWithModel(config, conversation, controller.signal);
        const draft = extractFragment(raw);

        // Insert as ordinary editable text (never auto-submit), then gate through the
        // same validation the user's own code goes through (FR-26.6/26.7).
        onDraft(draft);
        const result = await validateDraft(draft);
        const errorCount =
          result?.diagnostics.filter((d) => d.severity === "error").length ?? 0;
        setAttempts((previous) => [
          ...previous,
          { fragment: draft, valid: result?.valid ?? null, errorCount },
        ]);

        if (result === null || result.valid) break;
        if (attempt === config.maxRetries) break;

        conversation.push({ role: "assistant", content: raw });
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
        {showConfig && (
          <div className="space-y-2" data-testid="nl-config">
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
              Recommended: local Ollama with <code>phi4-mini</code> (MIT weights + MIT
              runtime). For browser access set <code>OLLAMA_ORIGINS</code> to this app's
              origin.
            </p>
          </div>
        )}

        {!configured && !showConfig && (
          <p className="text-fg-faint" data-testid="nl-disabled-hint">
            No model backend configured — the editor is fully usable without it. Set an
            endpoint under “configure” (e.g. local Ollama + phi4-mini).
          </p>
        )}

        {configured && (
          <>
            {needsLeaveNotice && (
              <div className="border-warn/50 text-warn rounded border p-2" data-testid="nl-leave-notice">
                The endpoint <code>{config.endpoint}</code> is not local: your description
                and the included type-surface context will leave this machine.
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
            </div>
            {attempts.length > 0 && (
              <ol className="space-y-1" data-testid="nl-attempts">
                {attempts.map((attempt, index) => (
                  <li key={index} className="flex items-center gap-1">
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
                      className="text-accent-2 cursor-pointer truncate hover:underline"
                      title={attempt.fragment}
                      onClick={() => onDraft(attempt.fragment)}
                    >
                      attempt {index + 1}
                      {attempt.valid === false && ` (${attempt.errorCount} error(s))`}
                    </button>
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
