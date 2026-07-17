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

/**
 * Stored-query surfaces shared by Path and Subgraph (concept spec §5.1/5.2): the
 * inline|stored source toggle, the kind-filtered picker with a read-only fragment
 * preview, and the "Save as stored query…" capture dialog. Management lives on the
 * Dashboard's Stored queries panel — the picker only points there.
 */

export const REGISTRATION_403 =
  "registration requires EnableDynamicCodeExecution on this instance — invoking stored queries does not";

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
                    ? "recompile failed on this instance — diagnostics on Dashboard → Stored queries"
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
        <span className="text-fg-faint pb-1 text-[11px]">manage on Dashboard</span>
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
            read-only — entries are immutable; delete &amp; re-register on Dashboard →
            Stored queries to change one.
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
    : save.error instanceof ApiError && save.error.status === 403
      ? REGISTRATION_403
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
        <Dialog.Portal>
          <Dialog.Overlay className="fixed inset-0 bg-black/60" />
          <Dialog.Content className="panel fixed top-1/2 left-1/2 w-[28rem] max-w-[90vw] -translate-x-1/2 -translate-y-1/2 p-4">
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
