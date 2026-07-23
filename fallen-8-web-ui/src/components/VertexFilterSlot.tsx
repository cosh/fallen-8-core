import type { InstanceConfig } from "../instances/types";
import { help } from "../lib/fieldHelp";
import { parseThreshold, type SemanticMetric, type SlotMode } from "../lib/semantic";
import { DelegateSlot } from "../delegate/DelegateSlot";

/**
 * A vertex-filter SLOT with its three fill modes (feature subgraph-semantic-thresholds):
 * match everything, a compiled C# fragment, or a declarative semantic threshold against
 * the request's semantic query. One owner per slot is structural here — the mode chooser
 * replaces the old "disabled with a reason" dance, and an inert semantic configuration
 * cannot be expressed. Used for the subgraph top-level vertex pre-filter and for every
 * Vertex pattern step.
 */
export function VertexFilterSlot({
  instance,
  idPrefix,
  label,
  contextLabel,
  mode,
  onModeChange,
  fragment,
  onFragmentChange,
  minScore,
  onMinScoreChange,
  metric,
}: {
  instance: InstanceConfig;
  idPrefix: string;
  label: string;
  contextLabel: string;
  mode: SlotMode;
  onModeChange: (mode: SlotMode) => void;
  fragment: string;
  onFragmentChange: (fragment: string) => void;
  /** Threshold text (parsed on build; non-finite blocks submit upstream). */
  minScore: string;
  onMinScoreChange: (minScore: string) => void;
  /** The request's semantic metric — decides whether the threshold is a floor or a ceiling. */
  metric: SemanticMetric;
}) {
  const minScoreInvalid = mode === "semantic" && parseThreshold(minScore) === undefined;

  return (
    <div className="space-y-1">
      <div className="flex flex-wrap items-center gap-2">
        <span
          className="text-fg-dim label-help text-[11px] tracking-widest uppercase"
          title={help("vertexSlotMode")}
        >
          {label}
        </span>
        <select
          className="input w-auto"
          data-testid={`${idPrefix}-mode`}
          value={mode}
          onChange={(e) => onModeChange(e.target.value as SlotMode)}
        >
          <option value="everything">match everything</option>
          <option value="fragment">C# fragment</option>
          <option value="semantic">semantic threshold</option>
        </select>
        {mode === "semantic" && (
          <label
            className="text-fg-dim label-help flex items-center gap-1 text-[12px]"
            title={help("slotSemanticThreshold")}
          >
            keep vertices scoring {metric === "L2" ? "≤" : "≥"}
            <input
              className="input w-24"
              data-testid={`${idPrefix}-minscore`}
              type="number"
              step="any"
              value={minScore}
              onChange={(e) => onMinScoreChange(e.target.value)}
            />
          </label>
        )}
        {minScoreInvalid && (
          <span className="text-warn text-[11px]" data-testid={`${idPrefix}-minscore-error`}>
            threshold must be a finite number
          </span>
        )}
      </div>
      {mode === "fragment" && (
        <DelegateSlot
          instance={instance}
          delegateKind="VertexFilter"
          label={label}
          contextLabel={contextLabel}
          value={fragment}
          onChange={onFragmentChange}
        />
      )}
    </div>
  );
}
