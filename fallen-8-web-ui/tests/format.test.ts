// MIT License
//
// format.test.ts
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
