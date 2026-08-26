// MIT License
//
// FileDropzone.tsx
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

import { useState, type DragEvent, type ReactNode } from "react";

/**
 * The drop half of taking a file from the user: the dashed target, the drag highlight, and the one
 * rule that is easy to get wrong (`preventDefault` on drag-over, or the browser navigates to the
 * file instead). Shared by the Knowledge screen's document ingest and the Integrations screen's
 * file settings, because a second copy of the drag handlers is a second thing to get wrong.
 *
 * What each screen keeps is what differs: Knowledge INGESTS on drop, Integrations STAGES for a run
 * that also needs an identity, and each owns its own file picker so the button next to it can mean
 * what that screen needs it to mean.
 */
export function FileDropzone(props: {
  /** Called with the dropped files, in the order the browser reported them, and never with none. */
  onFiles: (files: File[]) => void;
  /**
   * Takes every file of one drop rather than the first. A source handed over as a SET of files
   * needs it: without it the rest of the drop is silently ignored, which reads as a lost file.
   */
  multiple?: boolean;
  /** Greys the target out and refuses drops, for a screen that is not ready to take one. */
  disabled?: boolean;
  /** What the target says when nothing is being dragged over it. */
  children: ReactNode;
  /** Defaults to `dropzone`. */
  testId?: string;
  /** Extra classes, for a screen whose layout needs its own margins. */
  className?: string;
}) {
  const {
    onFiles,
    multiple = false,
    disabled = false,
    children,
    testId = "dropzone",
    className = "",
  } = props;
  const [dragging, setDragging] = useState(false);

  return (
    <div
      className={`rounded border border-dashed p-4 text-center text-[12px] ${
        dragging ? "border-accent text-accent" : "border-line text-fg-faint"
      } ${disabled ? "opacity-50" : ""} ${className}`}
      onDragOver={(e) => {
        e.preventDefault();
        if (!disabled) setDragging(true);
      }}
      onDragLeave={() => setDragging(false)}
      onDrop={(e: DragEvent<HTMLDivElement>) => {
        e.preventDefault();
        setDragging(false);
        if (disabled) return;
        const dropped = e.dataTransfer.files ? Array.from(e.dataTransfer.files) : [];
        const taken = multiple ? dropped : dropped.slice(0, 1);
        if (taken.length > 0) onFiles(taken);
      }}
      data-testid={testId}
    >
      {children}
    </div>
  );
}
