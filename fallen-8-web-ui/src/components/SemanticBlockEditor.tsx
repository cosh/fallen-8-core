import { Field } from "./Field";
import { help } from "../lib/fieldHelp";
import { parseVector } from "../lib/vector";
import {
  buildSemanticSpec,
  type SemanticDraft,
  type SemanticMetric,
  type SemanticSource,
} from "../lib/semantic";

/**
 * The declarative semantic-traversal block editor (feature element-embeddings), shared by
 * the Path and Subgraph screens. It carries the query source (a pasted vector, or text the
 * embedding provider embeds once), the embedding name / metric, and the code-free minScore
 * filter / costBySimilarity cost. The block is pure data — it works with dynamic code
 * execution OFF, which is the whole point, so it stays enabled even where the C# fragment
 * editors are 403-disabled. Full rules: features/done/element-embeddings README.
 *
 * Controlled: the parent owns the SemanticDraft (persisted for Path, local for Subgraph)
 * and derives which delegate slots to disable from semanticOwnsVertex{Filter,Cost}.
 */
export function SemanticBlockEditor({
  draft,
  onChange,
  allowCost,
  costDisabledReason,
  providerEnabled,
  embeddingNames,
  idPrefix,
  disabled = false,
  disabledReason,
}: {
  draft: SemanticDraft;
  onChange: (patch: Partial<SemanticDraft>) => void;
  /** costBySimilarity is a path concept; false on the subgraph screen. */
  allowCost: boolean;
  /** When set (e.g. algorithm is BLS, which ignores costs), the cost checkbox is disabled
   *  with this reason — distinct from the internal DotProduct rule. */
  costDisabledReason?: string;
  /** Resolved provider state; null = unknown (no graph shape computed yet). */
  providerEnabled: boolean | null;
  embeddingNames: string[];
  idPrefix: string;
  /** e.g. a stored template is selected — the block cannot apply. */
  disabled?: boolean;
  disabledReason?: string;
}) {
  const namesListId = `${idPrefix}-embedding-names`;
  const textUnavailable = providerEnabled !== true;
  const build = buildSemanticSpec(draft, { allowCost, providerEnabled });
  const parsedVector =
    draft.enabled && draft.source === "vector" && draft.vectorText.trim()
      ? parseVector(draft.vectorText)
      : null;

  if (disabled) {
    return (
      <div className="border-line rounded border p-2" data-testid={`${idPrefix}-semantic-disabled`}>
        <div className="text-fg-faint text-[11px] tracking-widest uppercase">
          semantic scoring
        </div>
        <p className="text-fg-faint mt-1 text-[11px]">
          {disabledReason ?? "not available here"}
        </p>
      </div>
    );
  }

  return (
    <div className="border-line rounded border" data-testid={`${idPrefix}-semantic`}>
      <div className="panel-title">
        <label className="label-help flex items-center gap-2" title={help("semanticQuerySource")}>
          <input
            type="checkbox"
            data-testid={`${idPrefix}-semantic-enable`}
            checked={draft.enabled}
            onChange={(e) => onChange({ enabled: e.target.checked })}
          />
          semantic scoring
        </label>
        <span className="text-fg-faint normal-case">
          similarity filter/cost · pure data, runs with dynamic code off
        </span>
      </div>

      {draft.enabled && (
        <div className="space-y-2 p-2">
          <div className="flex flex-wrap items-end gap-3">
            <Field
              helpKey="semanticQuerySource"
              label="query source"
              htmlFor={`${idPrefix}-sem-source`}
            >
              <select
                id={`${idPrefix}-sem-source`}
                data-testid={`${idPrefix}-sem-source`}
                className="input w-auto"
                value={draft.source}
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
                value={draft.embeddingName}
                onChange={(e) => onChange({ embeddingName: e.target.value })}
                placeholder="default"
              />
            </Field>
            <Field helpKey="semanticMetric" label="metric" htmlFor={`${idPrefix}-sem-metric`}>
              <select
                id={`${idPrefix}-sem-metric`}
                data-testid={`${idPrefix}-sem-metric`}
                className="input w-auto"
                value={draft.metric}
                onChange={(e) => {
                  const metric = e.target.value as SemanticMetric;
                  // DotProduct has no cost mapping; clear a stale checked cost so it can't
                  // strand the (now-disabled) checkbox in a checked state that blocks submit.
                  onChange(
                    metric === "DotProduct"
                      ? { metric, costBySimilarity: false }
                      : { metric },
                  );
                }}
              >
                <option>Cosine</option>
                <option>DotProduct</option>
                <option>L2</option>
              </select>
            </Field>
          </div>

          {draft.source === "vector" ? (
            <Field
              helpKey="semanticQueryVector"
              label="query vector (JSON array or comma-separated floats)"
              htmlFor={`${idPrefix}-sem-vector`}
            >
              <textarea
                id={`${idPrefix}-sem-vector`}
                data-testid={`${idPrefix}-sem-vector`}
                className="input h-14 w-full font-mono"
                value={draft.vectorText}
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
                value={draft.queryText}
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
                    ? "provider status unknown — Compute the Graph shape (Analytics), or paste a vector."
                    : "the embedding provider is off on this instance — paste a vector instead."}
                </div>
              )}
            </Field>
          )}

          <div className="flex flex-wrap items-end gap-3">
            <label
              className="text-fg-dim label-help flex items-center gap-1 text-[12px]"
              title={help("semanticMinScore")}
            >
              <input
                type="checkbox"
                data-testid={`${idPrefix}-sem-minscore-enable`}
                checked={draft.minScoreEnabled}
                onChange={(e) => onChange({ minScoreEnabled: e.target.checked })}
              />
              minScore filter
            </label>
            {draft.minScoreEnabled && (
              <Field
                helpKey="semanticMinScore"
                label={draft.metric === "L2" ? "minScore (≤ = closer)" : "minScore (≥ = closer)"}
                htmlFor={`${idPrefix}-sem-minscore`}
              >
                <input
                  id={`${idPrefix}-sem-minscore`}
                  data-testid={`${idPrefix}-sem-minscore`}
                  className="input w-24"
                  type="number"
                  step="any"
                  value={draft.minScore}
                  onChange={(e) => onChange({ minScore: e.target.value })}
                />
              </Field>
            )}
            {allowCost && (
              <label
                className="text-fg-dim label-help flex items-center gap-1 text-[12px]"
                title={help("semanticCostBySimilarity")}
              >
                <input
                  type="checkbox"
                  data-testid={`${idPrefix}-sem-cost`}
                  checked={draft.costBySimilarity}
                  disabled={draft.metric === "DotProduct" || Boolean(costDisabledReason)}
                  onChange={(e) => onChange({ costBySimilarity: e.target.checked })}
                />
                costBySimilarity (DIJKSTRA)
                {draft.metric === "DotProduct" ? (
                  <span className="text-fg-faint">— not under DotProduct</span>
                ) : (
                  costDisabledReason && (
                    <span className="text-fg-faint" data-testid={`${idPrefix}-sem-cost-disabled`}>
                      — {costDisabledReason}
                    </span>
                  )
                )}
              </label>
            )}
          </div>

          {!build.ok && (
            <p className="text-warn text-[11px]" data-testid={`${idPrefix}-sem-error`}>
              {build.error}
            </p>
          )}

          <datalist id={namesListId}>
            {embeddingNames.map((name) => (
              <option key={name} value={name} />
            ))}
          </datalist>
        </div>
      )}
    </div>
  );
}
