import { useInstanceStore } from "../instances/registry";
import { Truncated } from "../components/Truncated";
import { SampleGraphsPanel } from "../components/SampleGraphsPanel";

/**
 * Samples (feature sample-graphs): the one-click demo gallery, promoted from the Dashboard
 * to its own rail entry so every card spans the full width and carries its "what you can
 * test" steps, with a tag bar to filter by capability. Namespace-scoped — a load replaces
 * the active namespace's graph (SampleGraphsPanel owns the loader + typed-confirm wipe).
 */
export function SamplesScreen() {
  const { instance } = useInstanceStore();
  const namespace = instance.namespace ?? "default";

  return (
    <div className="mx-auto max-w-5xl space-y-4">
      <h1 className="text-fg flex min-w-0 items-baseline gap-1 text-sm font-bold tracking-wider uppercase">
        <span className="shrink-0">Samples —</span>
        <Truncated text={instance.name} max={24} />
        <span className="shrink-0">/</span>
        <Truncated text={namespace} max={32} />
      </h1>
      <p className="text-fg-dim text-[12px]">
        Curated graphs that load in one click — each comes styled for the canvas, indexed
        where it helps, and paired with example steps. Loading a sample erases the active
        graph first (behind a typed confirm); save a checkpoint or switch namespaces to keep
        what you have.
      </p>

      <SampleGraphsPanel />
    </div>
  );
}
