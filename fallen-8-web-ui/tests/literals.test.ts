import { describe, expect, it } from "vitest";
import {
  parseCreationDate,
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

/** Creation-date input on the mutation forms (feature studio-mutations-ux). */
describe("parseCreationDate", () => {
  it("empty keeps today's behaviour: 0 (epoch)", () => {
    expect(parseCreationDate("")).toEqual({ ok: true, seconds: 0 });
    expect(parseCreationDate("   ")).toEqual({ ok: true, seconds: 0 });
  });

  it("digit input is taken as unix SECONDS verbatim, bounded to uint", () => {
    expect(parseCreationDate("1713862800")).toEqual({ ok: true, seconds: 1713862800 });
    expect(parseCreationDate(" 0 ")).toEqual({ ok: true, seconds: 0 });
    expect(parseCreationDate("4294967295")).toEqual({ ok: true, seconds: 4294967295 });
    expect(parseCreationDate("4294967296").ok).toBe(false);
  });

  it("ISO date/times convert to unix seconds; offset-less input is UTC on every machine", () => {
    expect(parseCreationDate("1970-01-01T00:02:00Z")).toEqual({ ok: true, seconds: 120 });
    // No offset given: treated as UTC, NOT browser-local time.
    expect(parseCreationDate("1970-01-01T00:02:00")).toEqual({ ok: true, seconds: 120 });
    expect(parseCreationDate("1970-01-02")).toEqual({ ok: true, seconds: 86400 });
    // An explicit offset is honoured.
    expect(parseCreationDate("1970-01-01T02:02:00+02:00")).toEqual({ ok: true, seconds: 120 });
    const result = parseCreationDate("2026-07-17T12:00:00Z");
    expect(result).toEqual({ ok: true, seconds: Date.UTC(2026, 6, 17, 12) / 1000 });
  });

  it("rejects garbage, negatives, decimals, non-ISO dates, and pre-epoch dates", () => {
    expect(parseCreationDate("not a date").ok).toBe(false);
    expect(parseCreationDate("-5").ok).toBe(false);
    // Date.parse would happily read these as dates ("12.5" → Dec 5); we must not.
    expect(parseCreationDate("12.5").ok).toBe(false);
    expect(parseCreationDate("1713862800.5").ok).toBe(false);
    expect(parseCreationDate("May 5 2026").ok).toBe(false);
    expect(parseCreationDate("1900-01-01T00:00:00Z").ok).toBe(false);
  });
});
