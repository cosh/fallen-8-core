// MIT License
//
// SectionHelp.tsx
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

import { useEffect, useRef, useState } from "react";
import { docUrl, sectionHelp } from "../lib/sectionHelp";

/**
 * Per-section "How does this work?" help (feature studio-section-help). Rendered ONCE by the
 * shell, keyed by the active nav leaf; it drops an anchored popover listing the 1-3 docs pages
 * that explain the current section (each opens the published docs site in a new tab). Renders
 * nothing for a leaf with no mapping in sectionHelp.ts. Mirrors the hand-rolled anchored
 * dropdown pattern from NamespaceSwitcher (open-gated mousedown/keydown listeners), with
 * focus returned to the trigger on close.
 */
export function SectionHelp({ sectionKey }: { sectionKey: string | null }) {
  const entry = sectionHelp(sectionKey);
  const [open, setOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);

  // Outside click / Escape close the popover; listeners live only while open.
  useEffect(() => {
    if (!open) return;
    const onMouseDown = (event: MouseEvent) => {
      if (!containerRef.current?.contains(event.target as Node)) setOpen(false);
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        setOpen(false);
        buttonRef.current?.focus();
      }
    };
    document.addEventListener("mousedown", onMouseDown);
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("mousedown", onMouseDown);
      document.removeEventListener("keydown", onKeyDown);
    };
  }, [open]);

  // A section without a mapping (or a non-section route) shows no button at all.
  if (!entry) return null;

  return (
    <div ref={containerRef} className="relative">
      <button
        ref={buttonRef}
        type="button"
        data-testid="section-help"
        aria-haspopup="dialog"
        aria-expanded={open}
        // No aria-label: the visible text "How does this work?" is the accessible name (WCAG
        // 2.5.3 Label in Name). The current section is conveyed by the title tooltip and by the
        // popover's own heading/aria-label when opened.
        title={entry.heading}
        onClick={() => setOpen((v) => !v)}
        className="border-line text-fg-dim hover:text-accent hover:border-accent/40 flex cursor-pointer items-center gap-1 rounded border px-1.5 py-0.5 text-[10px] tracking-wider uppercase transition-colors"
      >
        <span aria-hidden>?</span>
        How does this work?
      </button>

      {open && (
        <div
          role="dialog"
          aria-label={entry.heading}
          data-testid="section-help-popover"
          className="panel border-line absolute top-full right-0 z-50 mt-1 w-80 border p-2 shadow-lg"
        >
          <div className="text-fg-dim px-2 pt-1 pb-2 text-[11px] font-semibold tracking-widest uppercase">
            {entry.heading}
          </div>
          <ul className="space-y-0.5">
            {entry.links.map((link) => (
              <li key={link.slug}>
                <a
                  href={docUrl(link.slug)}
                  target="_blank"
                  rel="noopener noreferrer"
                  data-testid="section-help-link"
                  onClick={() => setOpen(false)}
                  className="hover:bg-panel-2 block rounded px-2 py-1.5"
                >
                  <span className="text-fg flex items-center gap-1 text-[12px] font-semibold">
                    {link.title}
                    <span aria-hidden className="text-fg-faint">
                      ↗
                    </span>
                  </span>
                  <span className="text-fg-dim block text-[11px]">{link.blurb}</span>
                </a>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}
