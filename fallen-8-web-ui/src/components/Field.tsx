// MIT License
//
// Field.tsx
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

import type { ReactNode } from "react";
import { help, type FieldHelpKey } from "../lib/fieldHelp";

/**
 * Labeled field with hover help (feature studio-mutations-ux): the title sits on the
 * WRAPPER, so hovering the label or the input itself shows the explanation. Help copy
 * lives only in lib/fieldHelp.ts; call sites pass a key, never text.
 */
export function Field({
  helpKey,
  label,
  htmlFor,
  className,
  children,
}: {
  helpKey: FieldHelpKey;
  label: ReactNode;
  htmlFor?: string;
  className?: string;
  children: ReactNode;
}) {
  return (
    <div className={className} title={help(helpKey)}>
      <label className="label label-help" htmlFor={htmlFor}>
        {label}
      </label>
      {children}
    </div>
  );
}
