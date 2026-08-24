// MIT License
//
// SamplesScreen.tsx
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

import { useInstanceStore } from "../instances/registry";
import { Truncated } from "../components/Truncated";
import { SampleGraphsPanel } from "../components/SampleGraphsPanel";

/**
 * Samples (feature sample-graphs): the one-click demo gallery. It has its own rail entry so every
 * card spans the full width and carries its "what you can test" steps, with a tag bar to filter
 * by capability. Namespace-scoped — a load replaces
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
