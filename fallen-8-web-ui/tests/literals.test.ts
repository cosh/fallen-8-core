import { describe, expect, it } from "vitest";
import {
  toLiteral,
  toPropertySpec,
  toWireValue,
  validateTypedValue,
} from "../src/lib/literals";

/** Typed-literal editor conversions (FR-9, spec §10 "UI unit"). */
describe("typed literal conversions", () => {
  it("validates Int32 range and shape", () => {
    expect(validateTypedValue({ type: "System.Int32", raw: "42" })).toBeNull();
    expect(validateTypedValue({ type: "System.Int32", raw: "-7" })).toBeNull();
    expect(validateTypedValue({ type: "System.Int32", raw: "4.2" })).not.toBeNull();
    expect(validateTypedValue({ type: "System.Int32", raw: "abc" })).not.toBeNull();
    expect(validateTypedValue({ type: "System.Int32", raw: "2147483648" })).not.toBeNull();
  });

  it("validates Int64, Double, Boolean, DateTime", () => {
    expect(validateTypedValue({ type: "System.Int64", raw: "9007199254740993" })).toBeNull();
    expect(validateTypedValue({ type: "System.Double", raw: "3.14" })).toBeNull();
    expect(validateTypedValue({ type: "System.Double", raw: "" })).not.toBeNull();
    expect(validateTypedValue({ type: "System.Boolean", raw: "TRUE" })).toBeNull();
    expect(validateTypedValue({ type: "System.Boolean", raw: "yes" })).not.toBeNull();
    expect(
      validateTypedValue({ type: "System.DateTime", raw: "2026-01-31T12:00:00" }),
    ).toBeNull();
    expect(validateTypedValue({ type: "System.DateTime", raw: "not a date" })).not.toBeNull();
  });

  it("strings are always valid, including empty", () => {
    expect(validateTypedValue({ type: "System.String", raw: "" })).toBeNull();
  });

  it("normalizes boolean casing on the wire, preserves string whitespace", () => {
    expect(toWireValue({ type: "System.Boolean", raw: " TRUE " })).toBe("true");
    expect(toWireValue({ type: "System.String", raw: "  spaced  " })).toBe("  spaced  ");
    expect(toWireValue({ type: "System.Int32", raw: " 42 " })).toBe("42");
  });

  it("builds the wire literal shape", () => {
    expect(toLiteral({ type: "System.Int32", raw: "30" })).toEqual({
      value: "30",
      fullQualifiedTypeName: "System.Int32",
    });
    expect(toPropertySpec("age", { type: "System.Int32", raw: "30" })).toEqual({
      propertyId: "age",
      propertyValue: "30",
      fullQualifiedTypeName: "System.Int32",
    });
  });
});
