import { help } from "../lib/fieldHelp";
import { Field } from "./Field";
import { SemanticQueryEditor } from "./SemanticQueryEditor";
import { buildSemanticSpec, type SemanticDraft } from "../lib/semantic";

/**
 * The Path screen's declarative semantic block (feature element-embeddings): the shared
 * query core (SemanticQueryEditor) plus the block-local minScore filter and
 * costBySimilarity cost. Pure data — it works with dynamic code execution OFF, which is
 * the whole point, so it stays enabled even where the C# fragment editors are
 * 403-disabled. The Subgraph screen does not use this block: there the thresholds live on
 * the vertex-filter slots themselves (feature subgraph-semantic-thresholds).
 * Full rules: features/done/element-embeddings README.
 *
 * Controlled: the parent owns the SemanticDraft and derives which delegate slots to
 * disable from semanticOwnsVertex{Filter,Cost}.
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
  /** costBySimilarity is a path concept; false disables the cost slot entirely. */
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
  const build = buildSemanticSpec(draft, { allowCost, providerEnabled });

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
          <SemanticQueryEditor
            query={draft}
            onChange={(patch) =>
              // DotProduct has no cost mapping; clear a stale checked cost so it can't
              // strand the (now-disabled) checkbox in a checked state that blocks submit.
              onChange(
                patch.metric === "DotProduct" ? { ...patch, costBySimilarity: false } : patch,
              )
            }
            providerEnabled={providerEnabled}
            embeddingNames={embeddingNames}
            idPrefix={idPrefix}
          />

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
        </div>
      )}
    </div>
  );
}
