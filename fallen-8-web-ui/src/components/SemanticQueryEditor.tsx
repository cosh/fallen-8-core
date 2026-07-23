import { Field } from "./Field";
import { parseVector } from "../lib/vector";
import type { SemanticMetric, SemanticQueryDraft, SemanticSource } from "../lib/semantic";

/**
 * The semantic QUERY fields (source, vector/text, embedding name, metric) — the one query
 * every semantic decision of a request scores against. Extracted so the Path screen's
 * block editor and the Subgraph screen's per-request query section render exactly one
 * implementation. Pure data — works with dynamic code execution off.
 */
export function SemanticQueryEditor({
  query,
  onChange,
  providerEnabled,
  embeddingNames,
  idPrefix,
}: {
  query: SemanticQueryDraft;
  onChange: (patch: Partial<SemanticQueryDraft>) => void;
  /** Resolved provider state; null = unknown (no graph shape computed yet). */
  providerEnabled: boolean | null;
  embeddingNames: string[];
  idPrefix: string;
}) {
  const namesListId = `${idPrefix}-embedding-names`;
  const textUnavailable = providerEnabled !== true;
  const parsedVector =
    query.source === "vector" && query.vectorText.trim() ? parseVector(query.vectorText) : null;

  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-end gap-3">
        <Field helpKey="semanticQuerySource" label="query source" htmlFor={`${idPrefix}-sem-source`}>
          <select
            id={`${idPrefix}-sem-source`}
            data-testid={`${idPrefix}-sem-source`}
            className="input w-auto"
            value={query.source}
            onChange={(e) => onChange({ source: e.target.value as SemanticSource })}
          >
            <option value="vector">pasted vector</option>
            <option value="text">query text (provider)</option>
          </select>
        </Field>
        <Field
          helpKey="semanticEmbeddingName"
          label="embedding name"
          htmlFor={`${idPrefix}-sem-name`}
        >
          <input
            id={`${idPrefix}-sem-name`}
            className="input w-32"
            list={namesListId}
            value={query.embeddingName}
            onChange={(e) => onChange({ embeddingName: e.target.value })}
            placeholder="default"
          />
        </Field>
        <Field helpKey="semanticMetric" label="metric" htmlFor={`${idPrefix}-sem-metric`}>
          <select
            id={`${idPrefix}-sem-metric`}
            data-testid={`${idPrefix}-sem-metric`}
            className="input w-auto"
            value={query.metric}
            onChange={(e) => onChange({ metric: e.target.value as SemanticMetric })}
          >
            <option>Cosine</option>
            <option>DotProduct</option>
            <option>L2</option>
          </select>
        </Field>
      </div>

      {query.source === "vector" ? (
        <Field
          helpKey="semanticQueryVector"
          label="query vector (JSON array or comma-separated floats)"
          htmlFor={`${idPrefix}-sem-vector`}
        >
          <textarea
            id={`${idPrefix}-sem-vector`}
            data-testid={`${idPrefix}-sem-vector`}
            className="input h-14 w-full font-mono"
            value={query.vectorText}
            onChange={(e) => onChange({ vectorText: e.target.value })}
            placeholder="[0.12, -0.5, 0.33]"
          />
          {parsedVector && (
            <div className="text-fg-faint text-[11px]">
              {parsedVector.ok
                ? `d=${parsedVector.vector.length} — must match the embedding dimension`
                : parsedVector.error}
            </div>
          )}
        </Field>
      ) : (
        <Field
          helpKey="semanticQueryText"
          label="query text"
          htmlFor={`${idPrefix}-sem-text`}
          className="grow"
        >
          <input
            id={`${idPrefix}-sem-text`}
            data-testid={`${idPrefix}-sem-text`}
            className="input w-full"
            value={query.queryText}
            disabled={textUnavailable}
            onChange={(e) => onChange({ queryText: e.target.value })}
            placeholder="red bicycles"
          />
          {textUnavailable && (
            <div
              className="text-warn text-[11px]"
              data-testid={`${idPrefix}-sem-text-unavailable`}
            >
              {providerEnabled === null
                ? "provider status not reported by this server — paste a vector instead."
                : "the embedding provider is off on this instance — paste a vector instead."}
            </div>
          )}
        </Field>
      )}

      <datalist id={namesListId}>
        {embeddingNames.map((name) => (
          <option key={name} value={name} />
        ))}
      </datalist>
    </div>
  );
}
