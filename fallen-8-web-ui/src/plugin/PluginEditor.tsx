// MIT License
//
// PluginEditor.tsx
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

import { useCallback, useEffect, useRef, useState } from "react";
import * as Dialog from "@radix-ui/react-dialog";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import Editor from "@monaco-editor/react";
import { setupMonaco } from "../delegate/monacoSetup";
import { F8_EDITOR_OPTIONS } from "../delegate/editorOptions";
import { registerAlgorithmPlugin, registerFunctionPlugin, validatePlugin } from "../api/endpoints";
import { ApiError } from "../api/client";
import type {
  AlgorithmContract,
  PluginAuthoringCategory,
  PluginValidationResult,
} from "../api/types";
import type { InstanceConfig } from "../instances/types";
import { Field } from "../components/Field";
import {
  ALGORITHM_CONTRACTS,
  contractInterface,
  DEFAULT_PLUGIN_NAME,
  scaffoldFor,
} from "./scaffolds";
import { PluginNlAssistPanel } from "./nl/PluginNlAssistPanel";
import { useStudioConfig } from "../app/studioConfig";

setupMonaco();

/**
 * The whole-type plugin authoring editor (feature plugin-registration §6): pick a category
 * (+ contract for an algorithm), load a per-category scaffold into a full-file Monaco C#
 * editor, compile-validate it debounced through POST /plugins/{category}/validate (the
 * whole-type sibling of the delegate editor's /delegates/validate loop), then register into
 * the addressed namespace. Registration + validate are gated: a 403 surfaces as a friendly
 * "registration is disabled" line, exactly like the delegate editor's 401 auth-gate message.
 *
 * The plugin validate contract returns a plain-text `error` (not positioned diagnostics), so
 * — unlike the delegate editor's markers.ts path — diagnostics render as text; the Monaco
 * setup and the full-file csharp editor are the shared pieces.
 */

export const PLUGIN_NAME = /^[A-Za-z0-9_-]{1,128}$/;

export const PLUGIN_GATE_MESSAGE =
  "plugin registration is disabled on this server (enable the dynamic-plugin capability to author plugins)";

const AUTH_MESSAGE = "This instance requires an API key (configure it on the Connect screen).";

type ValidationState =
  | { phase: "idle" }
  | { phase: "validating" }
  | { phase: "done"; key: string; result: PluginValidationResult }
  | { phase: "gate"; message: string }
  | { phase: "error"; message: string };

/** Everything a validate/register call keys on — text drift re-blocks registration. */
function specKey(
  category: PluginAuthoringCategory,
  contract: AlgorithmContract,
  name: string,
  source: string,
): string {
  return [category, category === "algorithm" ? contract : "", name.trim(), source].join("");
}

export function PluginEditor({
  instance,
  onRegistered,
  onCancel,
}: {
  instance: InstanceConfig;
  onRegistered: (name: string) => void;
  onCancel: () => void;
}) {
  const queryClient = useQueryClient();
  const [category, setCategory] = useState<PluginAuthoringCategory>("algorithm");
  const [contract, setContract] = useState<AlgorithmContract>("Path");
  const [name, setName] = useState(DEFAULT_PLUGIN_NAME.algorithm);
  const [description, setDescription] = useState("");
  const [source, setSource] = useState(() =>
    scaffoldFor("algorithm", "Path", DEFAULT_PLUGIN_NAME.algorithm),
  );
  const [validation, setValidation] = useState<ValidationState>({ phase: "idle" });
  const abortRef = useRef<AbortController | null>(null);
  // Set by the NL panel while its generate→validate→refine loop owns validation.
  const drivingRef = useRef(false);
  // Embed policy: gates the whole NL column below (hoisted so the hook runs unconditionally).
  const nlAssistPolicy = useStudioConfig().nlAssist;
  // The last scaffold we generated: while the source still equals it (untouched), a
  // category/contract/name change re-scaffolds; once the user edits, we leave the source be.
  const scaffoldRef = useRef(source);

  const nameValid = PLUGIN_NAME.test(name.trim());
  const key = specKey(category, contract, name, source);

  useEffect(() => {
    const next = scaffoldFor(category, contract, name);
    if (source === scaffoldRef.current) {
      scaffoldRef.current = next;
      setSource(next);
    }
  }, [category, contract, name, source]);

  // Validates an EXPLICIT source (not the editor state), so the NL-assist loop can validate the
  // exact draft it just produced without waiting for a re-render, and the badge always reflects
  // whatever was last validated. Keys the result on the spec of that same source.
  const runValidation = useCallback(
    async (candidateSource: string): Promise<PluginValidationResult | null> => {
      abortRef.current?.abort();
      const controller = new AbortController();
      abortRef.current = controller;
      const candidateKey = specKey(category, contract, name, candidateSource);
      setValidation({ phase: "validating" });
      try {
        const result = await validatePlugin(
          instance,
          category,
          {
            name: name.trim(),
            contract: category === "algorithm" ? contract : undefined,
            sourceCode: candidateSource,
          },
          controller.signal,
        );
        if (controller.signal.aborted) return null;
        const final = result ?? { valid: false, error: "empty validation response" };
        setValidation({ phase: "done", key: candidateKey, result: final });
        return final;
      } catch (error) {
        if (controller.signal.aborted) return null;
        if (error instanceof ApiError && error.status === 403) {
          setValidation({ phase: "gate", message: PLUGIN_GATE_MESSAGE });
        } else if (error instanceof ApiError && error.status === 401) {
          setValidation({ phase: "gate", message: AUTH_MESSAGE });
        } else {
          setValidation({
            phase: "error",
            message: error instanceof Error ? error.message : String(error),
          });
        }
        return null;
      }
    },
    [instance, category, contract, name],
  );

  // Debounced compile-as-you-type (mirrors DelegateEditor's runValidation debounce). Suppressed
  // while NL-assist drives its own validate-and-refine loop, so the debounce cannot abort the
  // loop's in-flight compile-check.
  useEffect(() => {
    if (drivingRef.current) return;
    if (!nameValid || source.trim() === "") {
      setValidation({ phase: "idle" });
      return;
    }
    const timer = window.setTimeout(() => {
      if (!drivingRef.current) void runValidation(source);
    }, 600);
    return () => window.clearTimeout(timer);
  }, [nameValid, source, runValidation]);

  useEffect(() => () => abortRef.current?.abort(), []);

  const register = useMutation({
    mutationFn: () =>
      category === "algorithm"
        ? registerAlgorithmPlugin(instance, {
            name: name.trim(),
            contract,
            description: description.trim() || undefined,
            sourceCode: source,
          })
        : registerFunctionPlugin(instance, {
            name: name.trim(),
            description: description.trim() || undefined,
            sourceCode: source,
          }),
    onSuccess: (summary) => {
      queryClient.invalidateQueries({ queryKey: [instance.id, "plugins"] });
      onRegistered(summary?.name ?? name.trim());
    },
  });

  // Registration is allowed only when the CURRENT text is what passed validation — editing
  // after a VALID result re-blocks it (the same guard the delegate editor's commit uses).
  const validatedOk =
    validation.phase === "done" && validation.result.valid && validation.key === key;
  const canRegister = nameValid && validatedOk && !register.isPending;

  const registerError = !register.isError
    ? null
    : register.error instanceof ApiError && register.error.status === 403
      ? PLUGIN_GATE_MESSAGE
      : register.error instanceof ApiError && register.error.status === 401
        ? AUTH_MESSAGE
        : register.error instanceof ApiError && register.error.status === 409
          ? register.error.body ||
            `'${name.trim()}' already exists, collides with a built-in, or the per-namespace quota was reached.`
          : register.error instanceof ApiError
            ? register.error.body || register.error.message
            : (register.error as Error).message;

  const resetScaffold = () => {
    const next = scaffoldFor(category, contract, name);
    scaffoldRef.current = next;
    setSource(next);
  };

  return (
    <Dialog.Root open onOpenChange={(open) => !open && onCancel()}>
      <Dialog.Portal>
        <Dialog.Overlay className="modal-overlay" />
        {/* Centered via inset+m-auto, NOT translate: a transform would become the containing
            block for Monaco's fixedOverflowWidgets (suggest/hover). */}
        <Dialog.Content className="panel fixed inset-0 z-50 m-auto flex h-[85vh] w-5xl max-w-[95vw] flex-col">
          <div className="panel-title">
            <Dialog.Title className="contents">register plugin</Dialog.Title>
            <span className="text-fg-faint ml-auto shrink-0 normal-case">
              implements {contractInterface(category, contract)}
            </span>
          </div>

          <div className="border-line flex flex-wrap items-end gap-3 border-b p-3">
            <Field helpKey="pluginCategory" label="category" htmlFor="plugin-category">
              <select
                id="plugin-category"
                data-testid="plugin-category"
                className="input w-auto"
                value={category}
                onChange={(e) => setCategory(e.target.value as PluginAuthoringCategory)}
              >
                <option value="algorithm">algorithm</option>
                <option value="function">function</option>
              </select>
            </Field>
            {category === "algorithm" && (
              <Field helpKey="pluginContract" label="contract" htmlFor="plugin-contract">
                <select
                  id="plugin-contract"
                  data-testid="plugin-contract"
                  className="input w-auto"
                  value={contract}
                  onChange={(e) => setContract(e.target.value as AlgorithmContract)}
                >
                  {ALGORITHM_CONTRACTS.map((c) => (
                    <option key={c} value={c}>
                      {c}
                    </option>
                  ))}
                </select>
              </Field>
            )}
            <Field
              helpKey="pluginName"
              label="name (must equal the type's PluginName)"
              htmlFor="plugin-name"
            >
              <input
                id="plugin-name"
                data-testid="plugin-name"
                className="input w-56"
                value={name}
                onChange={(e) => setName(e.target.value)}
              />
            </Field>
            <Field
              helpKey="pluginDescription"
              label="description (optional)"
              htmlFor="plugin-description"
              className="grow"
            >
              <input
                id="plugin-description"
                className="input"
                value={description}
                onChange={(e) => setDescription(e.target.value)}
              />
            </Field>
            <button
              type="button"
              className="btn"
              data-testid="plugin-reset-scaffold"
              title="Replace the editor with a fresh scaffold for this category/contract/name"
              onClick={resetScaffold}
            >
              Reset to scaffold
            </button>
          </div>

          <div className="flex min-h-0 flex-1">
            <div className="min-h-0 flex-1">
              <Editor
                language="csharp"
                theme="vs-dark"
                value={source}
                onChange={(value) => setSource(value ?? "")}
                options={F8_EDITOR_OPTIONS}
              />
            </div>
            {/* Whole-type NL-assist (feature plugin-registration §6): drafts a complete plugin
                type from a plain-language intent, then runs it through the same compile-check
                the author's own source uses. Reuses the delegate NL backend/transport. The
                COLUMN is gated too: unlike the delegate editor's aside (which also holds the
                snippets), this panel is the column's only content, and a disabled embed must
                not reserve a dead 20rem gutter beside the code editor. */}
            {nlAssistPolicy !== "disabled" && (
              <div className="w-80 shrink-0" data-testid="plugin-nl-panel">
                <PluginNlAssistPanel
                  category={category}
                  contract={contract}
                  name={name.trim()}
                  scaffold={scaffoldFor(category, contract, name)}
                  currentSource={source}
                  onDraft={(code) => setSource(code)}
                  validateDraft={runValidation}
                  drivingRef={drivingRef}
                />
              </div>
            )}
          </div>

          {(validation.phase === "gate" ||
            validation.phase === "error" ||
            (validation.phase === "done" && !validation.result.valid && validation.key === key) ||
            registerError) && (
            <div className="border-line max-h-40 overflow-auto border-t p-3" data-testid="plugin-diagnostics">
              {validation.phase === "gate" && <p className="text-warn text-[12px]">{validation.message}</p>}
              {validation.phase === "error" && (
                <p className="text-danger text-[12px]">validation failed: {validation.message}</p>
              )}
              {validation.phase === "done" &&
                !validation.result.valid &&
                validation.key === key &&
                validation.result.error && (
                  <pre className="border-danger/40 text-danger rounded border p-2 text-[11px] wrap-break-word whitespace-pre-wrap">
                    {validation.result.error}
                  </pre>
                )}
              {registerError && (
                <p className="text-danger mt-2 text-[12px] wrap-break-word" data-testid="plugin-register-error">
                  {registerError}
                </p>
              )}
            </div>
          )}

          <div className="border-line flex items-center gap-2 border-t px-3 py-2">
            <PluginValidationBadge state={validation} currentKey={key} nameValid={nameValid} />
            <button
              type="button"
              className="btn ml-auto"
              data-testid="plugin-validate"
              disabled={!nameValid || source.trim() === "" || validation.phase === "validating"}
              onClick={() => void runValidation(source)}
            >
              Validate
            </button>
            <button type="button" className="btn" onClick={onCancel}>
              Cancel
            </button>
            <button
              type="button"
              className="btn btn-accent"
              data-testid="plugin-register"
              disabled={!canRegister}
              title={
                canRegister
                  ? undefined
                  : "Blocked: the source must pass validation for the current name/contract first."
              }
              onClick={() => register.mutate()}
            >
              {register.isPending ? "Registering…" : "Register"}
            </button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

function PluginValidationBadge({
  state,
  currentKey,
  nameValid,
}: {
  state: ValidationState;
  currentKey: string;
  nameValid: boolean;
}) {
  if (!nameValid) {
    return (
      <span className="text-warn text-[11px]" data-testid="plugin-name-invalid">
        name must match ^[A-Za-z0-9_-] (max 128)
      </span>
    );
  }
  if (state.phase === "done" && state.key !== currentKey) {
    return <span className="text-fg-faint text-[11px]">edited — not validated</span>;
  }
  switch (state.phase) {
    case "idle":
      return <span className="text-fg-faint text-[11px]">not validated</span>;
    case "validating":
      return <span className="text-fg-dim text-[11px]">validating…</span>;
    case "done":
      return state.result.valid ? (
        <span className="text-accent text-[11px] font-semibold" data-testid="plugin-valid">
          VALID — compiles &amp; satisfies the contract
        </span>
      ) : (
        <span className="text-danger text-[11px] font-semibold" data-testid="plugin-invalid">
          INVALID — see diagnostics
        </span>
      );
    case "gate":
      return (
        <span className="text-warn min-w-0 truncate text-[11px]" data-testid="plugin-gate" title={state.message}>
          {state.message}
        </span>
      );
    case "error":
      return (
        <span className="text-danger min-w-0 truncate text-[11px]" title={state.message}>
          validation failed: {state.message}
        </span>
      );
  }
}
