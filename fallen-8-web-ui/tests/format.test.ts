import { describe, expect, it } from "vitest";
import { formatCompact, formatExact } from "../src/lib/format";

describe("stat formatting", () => {
  it("compacts large TPS numbers", () => {
    expect(formatCompact(592134058.33)).toBe("592.1M");
    expect(formatCompact(1234)).toBe("1.2K");
    expect(formatCompact(0)).toBe("0");
  });

  it("renders exact numbers with grouping", () => {
    expect(formatExact(10001000)).toBe("10,001,000");
    expect(formatExact(0)).toBe("0");
  });

  it("degrades to a dash on non-finite input", () => {
    expect(formatCompact(Number.NaN)).toBe("—");
    expect(formatExact(Number.POSITIVE_INFINITY)).toBe("—");
  });
});
