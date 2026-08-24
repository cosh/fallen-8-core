// MIT License
//
// EmbeddingsTab.tsx
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
import { useMutation } from "@tanstack/react-query";
import {
  deleteElementEmbedding,
  embedElement,
  putElementEmbedding,
} from "../api/endpoints";
import type { EdgeREST, VertexREST } from "../api/types";
import { formatPropertyValue } from "../lib/literals";
import { parseVector } from "../lib/vector";
import { isReservedEmbeddingProperty, previewVector } from "../lib/embeddingProperties";
import { similarSearchesFor } from "../lib/findSimilar";
import { isEdge } from "../lib/hydrate";
import { useStatus } from "../state/status";
import { useInstanceStore } from "../instances/registry";
import { useNavigate } from "@tanstack/react-router";
import { DISPLAY_CAP } from "../lib/truncate";
import { Truncated } from "./Truncated";
import { ErrorBox } from "./ErrorBox";
import { Field } from "./Field";
import {
  EMBEDDING_PROPERTY_PREFIX as EMBEDDING_PREFIX,
  EMBEDDING_MODEL_PROPERTY_PREFIX as EMBEDDING_MODEL_PREFIX,
} from "../state/graphShape";
import type { InstanceConfig } from "../instances/types";

/**
 * Named-embedding management for one element (feature element-embeddings): lists the
 * element's embeddings (folded out of the plain property table), and sets/replaces/removes
 * one via the typed endpoints. Bring-your-own-vector (paste) is always available; text-in
 * needs the embedding provider. onRefresh re-fetches the element so the list reflects the
 * write. Server 400/404/409 reasons render verbatim.
 */
export function EmbeddingsTab({
  instance,
  element,
  providerEnabled,
  onRefresh,
}: {
  instance: InstanceConfig;
  element: VertexREST | EdgeREST;
  providerEnabled: boolean | null;
  onRefresh: () => void;
}) {
  // "Find similar" per embedding, but ONLY where a bound vector index actually projects that
  // name: an unbound index holds vectors that exist nowhere else, so it cannot be expected to
  // contain this element, and a bound index of another name projects different vectors.
  const navigate = useNavigate();
  const { store } = useInstanceStore();
  const setScanPrefill = store((s) => s.setScanPrefill);
  const inventory = useStatus(instance).data?.indices;
  const similar = similarSearchesFor(element, inventory, isEdge(element));

  const properties = element.properties ?? [];
  const embeddings = properties
    .filter((p) => p.propertyId.startsWith(EMBEDDING_PREFIX))
    .map((p) => {
      const name = p.propertyId.slice(EMBEDDING_PREFIX.length);
      const stamp = properties.find((s) => s.propertyId === EMBEDDING_MODEL_PREFIX + name);
      return { name, value: p.propertyValue, model: stamp?.propertyValue ?? null };
    });

  const [name, setName] = useState("default");
  const [source, setSource] = useState<"vector" | "text">("vector");
  const [vectorText, setVectorText] = useState("");
  const [text, setText] = useState("");
  const textUnavailable = providerEnabled !== true;

  // Build-from-element (feature embedding-out-of-box): compose the text to embed from the
  // element's label + plain properties. Everything is included by default (one click =
  // embed the whole element); unchecking narrows. Tracking EXCLUSIONS keeps the default
  // all-in and makes stale ids from a previous lookup harmless.
  const LABEL_KEY = "$label";
  const [excluded, setExcluded] = useState<ReadonlySet<string>>(new Set());
  const buildable: { key: string; caption: string; line: string }[] = [
    ...(element.label
      ? [{ key: LABEL_KEY, caption: "label", line: `label: ${element.label}` }]
      : []),
    ...properties
      .filter((p) => !isReservedEmbeddingProperty(p.propertyId))
      .map((p) => ({
        key: p.propertyId,
        caption: p.propertyId,
        line: `${p.propertyId}: ${formatPropertyValue(p.propertyValue)}`,
      })),
  ];
  const composedText = buildable
    .filter((b) => !excluded.has(b.key))
    .map((b) => b.line)
    .join("\n");
  const toggleBuildKey = (key: string) =>
    setExcluded((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });

  const write = useMutation({
    mutationFn: async () => {
      const trimmedName = name.trim();
      if (source === "text") {
        await embedElement(instance, {
          graphElementId: element.id,
          text,
          name: trimmedName || undefined,
        });
        return;
      }
      const parsed = parseVector(vectorText);
      if (!parsed.ok) throw new Error(`Vector: ${parsed.error}.`);
      await putElementEmbedding(instance, element.id, trimmedName, { vector: parsed.vector });
    },
    onSuccess: () => {
      setVectorText("");
      setText("");
      onRefresh();
    },
  });

  const remove = useMutation({
    mutationFn: (embeddingName: string) =>
      deleteElementEmbedding(instance, element.id, embeddingName),
    onSuccess: onRefresh,
  });

  return (
    <div className="space-y-3" data-testid="embeddings-tab">
      {embeddings.length === 0 ? (
        <div className="text-fg-faint">no embeddings on this element</div>
      ) : (
        <table className="w-full">
          <thead>
            <tr className="text-fg-faint">
              <th className="table-cell">name</th>
              <th className="table-cell">vector</th>
              <th className="table-cell">model</th>
              <th className="table-cell" />
            </tr>
          </thead>
          <tbody>
            {embeddings.map((e) => (
              <tr key={e.name} data-testid={`embedding-row-${e.name}`}>
                <td className="table-cell font-semibold">
                  <Truncated text={e.name} max={DISPLAY_CAP.name} />
                </td>
                <td className="table-cell font-mono">{previewVector(e.value)}</td>
                <td className="table-cell text-fg-dim">
                  <Truncated
                    text={typeof e.model === "string" && e.model ? e.model : "—"}
                    max={DISPLAY_CAP.name}
                  />
                </td>
                <td className="table-cell">
                  {similar.some((candidate) => candidate.embeddingName === e.name) && (
                    <button
                      type="button"
                      className="btn"
                      data-testid={`embedding-similar-${e.name}`}
                      title="search the bound vector index with this element's own vector"
                      onClick={() => {
                        const match = similar.find((c) => c.embeddingName === e.name)!;
                        setScanPrefill(match.prefill);
                        void navigate({ to: "/query" });
                      }}
                    >
                      Find similar
                    </button>
                  )}
                  <button
                    type="button"
                    className="btn btn-danger"
                    data-testid={`embedding-remove-${e.name}`}
                    disabled={remove.isPending}
                    onClick={() => remove.mutate(e.name)}
                  >
                    Remove
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <form
        className="border-line flex flex-wrap items-end gap-2 border-t pt-3"
        onSubmit={(event) => {
          event.preventDefault();
          write.mutate();
        }}
      >
        <Field helpKey="embeddingName" label="name" htmlFor="emb-name">
          <input
            id="emb-name"
            data-testid="emb-name"
            className="input w-28"
            value={name}
            onChange={(event) => setName(event.target.value)}
          />
        </Field>
        <div className="border-line flex overflow-hidden rounded border">
          {(["vector", "text"] as const).map((mode) => (
            <button
              key={mode}
              type="button"
              data-testid={`emb-source-${mode}`}
              className={`px-2 py-1 text-[11px] ${
                source === mode ? "bg-panel-2 text-accent" : "text-fg-dim hover:text-fg"
              }`}
              onClick={() => setSource(mode)}
            >
              {mode}
            </button>
          ))}
        </div>
        {source === "vector" ? (
          <Field
            helpKey="embeddingVectorPaste"
            label="vector"
            htmlFor="emb-vector"
            className="grow"
          >
            <input
              id="emb-vector"
              data-testid="emb-vector"
              className="input w-full font-mono"
              value={vectorText}
              onChange={(event) => setVectorText(event.target.value)}
              placeholder="[0.12, -0.5, 0.33]"
            />
          </Field>
        ) : (
          <Field helpKey="embeddingText" label="text" htmlFor="emb-text" className="grow">
            <textarea
              id="emb-text"
              data-testid="emb-text"
              className="input w-full"
              rows={2}
              value={text}
              disabled={textUnavailable}
              onChange={(event) => setText(event.target.value)}
              placeholder="a red bicycle"
            />
            {textUnavailable && (
              <div className="text-warn text-[11px]" data-testid="emb-text-unavailable">
                {providerEnabled === null
                  ? "provider status not reported by this server — paste a vector instead."
                  : "the embedding provider is off on this instance — paste a vector instead."}
              </div>
            )}
            {!textUnavailable && buildable.length === 0 && (
              <div className="text-fg-faint text-[11px]" data-testid="emb-build-empty">
                build from element: nothing to build from — this element has no label and
                no plain properties; type the text yourself.
              </div>
            )}
            {!textUnavailable && buildable.length > 0 && (
              <div
                className="text-fg-dim flex flex-wrap items-center gap-2 text-[11px]"
                data-testid="emb-build"
              >
                <span className="text-fg-faint">build from element:</span>
                {buildable.map((b) => (
                  <label key={b.key} className="flex items-center gap-1">
                    <input
                      type="checkbox"
                      data-testid={`emb-build-${b.key}`}
                      checked={!excluded.has(b.key)}
                      onChange={() => toggleBuildKey(b.key)}
                    />
                    {b.caption}
                  </label>
                ))}
                <button
                  type="button"
                  className="btn"
                  data-testid="emb-build-fill"
                  disabled={!composedText}
                  onClick={() => setText(composedText)}
                  title="Fill the text field from the checked label/properties; edit before setting if you like"
                >
                  Fill text
                </button>
              </div>
            )}
          </Field>
        )}
        <button
          type="submit"
          className="btn btn-accent"
          data-testid="emb-write"
          disabled={
            !name.trim() ||
            write.isPending ||
            (source === "vector" ? !vectorText.trim() : textUnavailable || !text.trim())
          }
        >
          {write.isPending ? "Writing…" : "Set embedding"}
        </button>
      </form>
      {(write.isError || remove.isError) && (
        <ErrorBox error={write.error ?? remove.error} />
      )}
    </div>
  );
}
