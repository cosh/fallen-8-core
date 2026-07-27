// MIT License
//
// FirstRunOverlay.tsx
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

import * as Dialog from "@radix-ui/react-dialog";
import { useNavigate } from "@tanstack/react-router";
import { useActiveNamespace } from "../instances/registry";
import { FirstRunShow } from "./FirstRunShow";
import { useFirstRun } from "./firstRunStore";

/**
 * Manual-replay overlay (feature studio-first-run). Renders the SAME <FirstRunShow> the
 * Dashboard auto-shows, on top of the current screen, from beat 1. Radix Dialog gives the focus
 * trap, Escape-to-close, and focus restore for free. Closing never touches the dismissed flag.
 *
 * The show creates NOTHING: its handoff buttons only navigate (to the Sample gallery or the
 * import screen) or dismiss. The unit-test graph endpoint is deliberately never wired in (see
 * CLAUDE.md); newcomers reach a populated graph through the curated Sample gallery.
 */
export function FirstRunOverlay() {
  const replayOpen = useFirstRun((s) => s.replayOpen);
  const closeReplay = useFirstRun((s) => s.closeReplay);
  const namespace = useActiveNamespace();
  const navigate = useNavigate();

  const onBrowseSamples = () => {
    closeReplay();
    void navigate({ to: "/q/$ns/samples", params: { ns: namespace } });
  };

  const onImport = () => {
    closeReplay();
    void navigate({ to: "/save-games" });
  };

  return (
    <Dialog.Root open={replayOpen} onOpenChange={(o) => !o && closeReplay()}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-black/70" />
        <Dialog.Content
          data-testid="first-run-overlay"
          className="panel fixed top-1/2 left-1/2 flex h-[min(680px,88vh)] w-[min(1000px,92vw)] -translate-x-1/2 -translate-y-1/2 flex-col p-4"
        >
          <div className="mb-2 flex items-center gap-2">
            <Dialog.Title className="text-fg-dim text-[11px] font-semibold tracking-widest uppercase">
              Fallen-8 intro
            </Dialog.Title>
            <Dialog.Close asChild>
              <button
                type="button"
                className="btn ml-auto"
                data-testid="first-run-overlay-close"
                aria-label="Close the intro"
              >
                Close
              </button>
            </Dialog.Close>
          </div>
          <div className="min-h-0 flex-1">
            <FirstRunShow
              variant="replay"
              onExplore={closeReplay}
              onBrowseSamples={onBrowseSamples}
              onImport={onImport}
            />
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
