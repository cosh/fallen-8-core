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
import { formatBytes, formatCompact, formatExact } from "../src/lib/format";

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

describe("byte sizes", () => {
  it("uses binary units and keeps whole bytes whole", () => {
    expect(formatBytes(512)).toBe("512 B");
    expect(formatBytes(1024)).toBe("1.0 KiB");
    expect(formatBytes(1536)).toBe("1.5 KiB");
    expect(formatBytes(134217728)).toBe("128.0 MiB");
  });

  it("reaches GiB rather than reporting gigabytes as thousands of MiB", () => {
    // The failure that motivated this: several gibibytes of files. A refusal that spells it in mebibytes
    // leaves the reader doing the division while being told no.
    expect(formatBytes(6274678784)).toBe("5.8 GiB");
    // And it saturates rather than inventing a TiB unit it has no entry for.
    expect(formatBytes(1024 ** 4)).toBe("1024.0 GiB");
  });

  it("reports nothing as 0 B, including the values a size should never be", () => {
    expect(formatBytes(0)).toBe("0 B");
    expect(formatBytes(-1)).toBe("0 B");
    // log(0) is -Infinity, so an unguarded exponent would index past the unit table and render
    // "NaN undefined" in the middle of a refusal.
    expect(formatBytes(Number.NaN)).toBe("0 B");
  });
});
