// MIT License
//
// StoredQueryControls.tsx
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

import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import * as Dialog from "@radix-ui/react-dialog";
import { getStoredQuery, listStoredQueries, registerStoredQuery } from "../api/endpoints";
import { ApiError } from "../api/client";
import type {
  StoredPathQueryBlock,
  StoredQueryKind,
  StoredQuerySpecification,
  StoredSubGraphQueryBlock,
} from "../api/types";
import type { InstanceConfig } from "../instances/types";
import type { FilterSource } from "../state/instanceStore";
import { describeStoredSpecification, STORED_QUERY_NAME } from "../lib/storedQueries";
import { ErrorBox } from "./ErrorBox";
import { Field } from "./Field";
import { usePortalContainer } from "../app/studioConfig";

/**
 * Stored-query surfaces shared by the two Traverse scenario tabs (concept spec §5.1/5.2):
 * the inline|stored source toggle, the kind-filtered picker with a read-only fragment
 * preview, and the "Save as stored query…" capture dialog. Management (list, source,
 * delete) lives in the Traverse screen's Stored queries tab; the picker only points there.
 */

export const REGISTRATION_401 =
  "registration requires the instance's API key — configure it on the Connect screen";

export function FilterSourceToggle({
  value,
  onChange,
}: {
  value: FilterSource;
  onChange: (source: FilterSource) => void;
}) {
  return (
    <div className="flex items-center gap-2">
      <span className="text-fg-dim text-[11px] tracking-wider uppercase">filters</span>
      <div className="border-line flex overflow-hidden rounded border">
        {(["inline", "stored"] as const).map((source) => (
          <button
            key={source}
            type="button"
            data-testid={`filter-source-${source}`}
            className={`px-2 py-1 text-[11px] ${
              value === source ? "bg-panel-2 text-accent" : "text-fg-dim hover:text-fg"
            }`}
            onClick={() => onChange(source)}
          >
            {source}
          </button>
        ))}
      </div>
    </div>
  );
}

/** Read-only fragment rows in the DelegateSlot chrome, minus Edit/Clear. */
function FragmentRow({ label, fragment }: { label: string; fragment: string }) {
  return (
    <div className="flex items-center gap-2">
      <span className="text-fg-dim w-44 shrink-0 text-[11px] tracking-wider uppercase">
        {label}
      </span>
      <code className="text-fg min-w-0 flex-1 truncate text-[11px]" title={fragment}>
        {fragment}
      </code>
    </div>
  );
}

export function StoredQueryPicker({
  instance,
  kind,
  value,
  onChange,
}: {
  instance: InstanceConfig;
  kind: StoredQueryKind;
  value: string;
  onChange: (name: string) => void;
}) {
  const list = useQuery({
    queryKey: [instance.id, "storedqueries"],
    queryFn: ({ signal }) => listStoredQueries(instance, signal),
  });
  const options = (list.data ?? []).filter((q) => q.kind === kind && q.name);
  const detail = useQuery({
    queryKey: [instance.id, "storedquery", value],
    queryFn: ({ signal }) => getStoredQuery(instance, value, signal),
    enabled: Boolean(value),
  });
  const preview = detail.data
    ? describeStoredSpecification(detail.data.kind, detail.data.specificationJson)
    : null;

  return (
    <div className="space-y-2" data-testid="stored-query-picker">
      <div className="flex flex-wrap items-end gap-2">
        <Field
          helpKey="storedQuery"
          label={`stored query (${kind})`}
          htmlFor={`stored-query-${kind}`}
        >
          <select
            id={`stored-query-${kind}`}
            data-testid="stored-query-select"
            className="input w-64"
            value={value}
            onChange={(e) => onChange(e.target.value)}
          >
            <option value="">— pick a stored query —</option>
            {options.map((q) => (
              <option
                key={q.name!}
                value={q.name!}
                disabled={q.compileState === "Failed"}
                title={
                  q.compileState === "Failed"
                    ? "recompile failed on this instance — diagnostics on the Stored queries tab"
                    : (q.description ?? undefined)
                }
              >
                {q.name}
                {q.compileState && q.compileState !== "Compiled"
                  ? ` — ${q.compileState}`
                  : ""}
              </option>
            ))}
          </select>
        </Field>
        <span className="text-fg-faint pb-1 text-[11px]">
          manage on the Stored queries tab
        </span>
      </div>
      {list.isError && <ErrorBox error={list.error} onRetry={() => list.refetch()} />}
      {list.isSuccess && options.length === 0 && (
        <p className="text-fg-faint text-[11px]">
          no stored queries of kind {kind} on this instance — author fragments inline,
          then “Save as stored query…”.
        </p>
      )}
      {value && preview && (
        <div className="space-y-1" data-testid="stored-query-preview">
          {preview.rows.map((row) => (
            <FragmentRow key={row.label} label={row.label} fragment={row.fragment} />
          ))}
          {preview.note && <p className="text-fg-faint text-[11px]">{preview.note}</p>}
          <p className="text-fg-faint text-[11px]">
            read-only — entries are immutable; delete &amp; re-register on the Stored queries
            tab to change one.
          </p>
        </div>
      )}
    </div>
  );
}

export function SaveAsStoredQuery({
  instance,
  kind,
  buildBlock,
  disabled,
  disabledReason,
  onSaved,
}: {
  instance: InstanceConfig;
  kind: StoredQueryKind;
  buildBlock: () => StoredPathQueryBlock | StoredSubGraphQueryBlock;
  disabled?: boolean;
  disabledReason?: string;
  onSaved: (name: string) => void;
}) {
  const [open, setOpen] = useState(false);
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const queryClient = useQueryClient();
  const portalContainer = usePortalContainer();

  const save = useMutation({
    mutationFn: () => {
      const trimmed = name.trim();
      const spec: StoredQuerySpecification =
        kind === "Path"
          ? {
              name: trimmed,
              kind,
              description: description.trim() || undefined,
              path: buildBlock() as StoredPathQueryBlock,
            }
          : {
              name: trimmed,
              kind,
              description: description.trim() || undefined,
              subGraph: buildBlock() as StoredSubGraphQueryBlock,
            };
      return registerStoredQuery(instance, spec);
    },
    onSuccess: (summary) => {
      queryClient.invalidateQueries({ queryKey: [instance.id, "storedqueries"] });
      const savedName = summary?.name ?? name.trim();
      setOpen(false);
      setName("");
      setDescription("");
      save.reset();
      onSaved(savedName);
    },
  });

  const nameValid = STORED_QUERY_NAME.test(name.trim());
  const errorText = !save.isError
    ? null
    : save.error instanceof ApiError && save.error.status === 401
      ? REGISTRATION_401
      : save.error instanceof ApiError && save.error.status === 409
        ? `'${name.trim()}' already exists — stored queries are immutable; pick another name or delete the existing one first.`
        : (save.error as Error).message;

  return (
    <>
      <button
        type="button"
        className="btn"
        data-testid="save-as-stored-query"
        disabled={disabled}
        title={disabled ? disabledReason : undefined}
        onClick={() => setOpen(true)}
      >
        Save as stored query…
      </button>
      <Dialog.Root
        open={open}
        onOpenChange={(o) => {
          if (!o) {
            setOpen(false);
            save.reset();
          }
        }}
      >
        <Dialog.Portal container={portalContainer}>
          <Dialog.Overlay className="modal-overlay" />
          <Dialog.Content className="panel modal-center w-[28rem] max-w-[90vw] p-4">
            <Dialog.Title className="text-fg text-sm font-bold">
              Save as stored query
            </Dialog.Title>
            <Dialog.Description className="text-fg-dim mt-2 text-[12px]">
              Registers the committed fragments as a named, pre-compiled {kind} query on
              this instance.
            </Dialog.Description>
            <Field
              helpKey="storedQueryName"
              label="name (A–Z a–z 0–9 _ - · max 128)"
              htmlFor="stored-query-name"
              className="mt-4"
            >
              <input
                id="stored-query-name"
                data-testid="stored-query-name"
                className="input"
                value={name}
                onChange={(e) => setName(e.target.value)}
                autoFocus
              />
            </Field>
            <Field
              helpKey="storedQueryDescription"
              label="description (optional)"
              htmlFor="stored-query-description"
              className="mt-3"
            >
              <input
                id="stored-query-description"
                className="input"
                value={description}
                onChange={(e) => setDescription(e.target.value)}
              />
            </Field>
            {errorText && (
              <p className="text-danger mt-3 text-[12px]" data-testid="stored-query-error">
                {errorText}
              </p>
            )}
            <div className="mt-4 flex justify-end gap-2">
              <button type="button" className="btn" onClick={() => setOpen(false)}>
                Cancel
              </button>
              <button
                type="button"
                className="btn btn-accent"
                data-testid="stored-query-register"
                disabled={!nameValid || save.isPending}
                onClick={() => save.mutate()}
              >
                {save.isPending ? "Registering…" : "Register"}
              </button>
            </div>
          </Dialog.Content>
        </Dialog.Portal>
      </Dialog.Root>
    </>
  );
}
