// MIT License
//
// field-help.test.tsx
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

import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { FIELD_HELP, help } from "../src/lib/fieldHelp";
import { Field } from "../src/components/Field";

/**
 * Portal-wide field help (feature studio-mutations-ux): the dictionary is the ONE home
 * for help copy, and Field puts it on the wrapper so label AND input hover show it.
 */
describe("field help dictionary", () => {
  it("every entry is a non-empty explanation, not a placeholder", () => {
    for (const [key, text] of Object.entries(FIELD_HELP)) {
      expect(text.trim().length, `FIELD_HELP.${key}`).toBeGreaterThan(20);
    }
  });
});

describe("Field", () => {
  it("puts the help text on the wrapper so hovering label or input shows it", () => {
    render(
      <Field helpKey="elementId" label="element id" htmlFor="f-x" className="w-24">
        <input id="f-x" />
      </Field>,
    );
    const label = screen.getByText("element id");
    const wrapper = label.parentElement!;
    expect(wrapper).toHaveAttribute("title", help("elementId"));
    expect(wrapper.className).toContain("w-24");
    expect(label).toHaveClass("label-help");
    expect(label).toHaveAttribute("for", "f-x");
    expect(wrapper).toContainElement(screen.getByRole("textbox"));
  });
});
