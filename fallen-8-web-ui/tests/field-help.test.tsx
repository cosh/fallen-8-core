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
