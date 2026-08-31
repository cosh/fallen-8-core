// MIT License
//
// file-limits.test.ts
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
import {
  LIMITS_UNKNOWN_NOTE,
  checkStaging,
  describeLimits,
  type SizedFile,
} from "../src/lib/fileLimits";
import type { FileLimits } from "../src/api/types";

const MiB = 1024 * 1024;

/** The shipped defaults, as the instance serves them. */
const shipped: FileLimits = {
  maxFileBytes: 128 * MiB,
  maxJobFileBytes: 512 * MiB,
  maxJobFiles: 256,
};

function file(name: string, size: number): SizedFile {
  return { name, size };
}

function many(count: number, size: number): SizedFile[] {
  return Array.from({ length: count }, (_, i) => file(`f${i}.arxml`, size));
}

describe("staging against the instance's own ceilings", () => {
  it("accepts a set that fits and says nothing", () => {
    const verdict = checkStaging({
      limits: shipped,
      incoming: [file("a.arxml", 10 * MiB), file("b.arxml", 20 * MiB)],
    });

    expect(verdict.accepted).toHaveLength(2);
    expect(verdict.problem).toBeNull();
  });

  it("refuses only the file that is individually too big, and stages its siblings", () => {
    // Per-file is the one ceiling a single file can break on its own, so it is the one ceiling
    // where dropping just the offender is the honest answer.
    const verdict = checkStaging({
      limits: shipped,
      incoming: [file("small.arxml", 1 * MiB), file("huge.arxml", 200 * MiB)],
    });

    expect(verdict.accepted.map((f) => f.name)).toEqual(["small.arxml"]);
    expect(verdict.problem).toContain("huge.arxml");
    expect(verdict.problem).toContain("200.0 MiB");
    expect(verdict.problem).toContain("128.0 MiB");
  });

  it("names every oversized file rather than only the first", () => {
    const verdict = checkStaging({
      limits: shipped,
      incoming: [file("a.arxml", 200 * MiB), file("b.arxml", 300 * MiB)],
    });

    expect(verdict.accepted).toEqual([]);
    expect(verdict.problem).toContain("a.arxml");
    expect(verdict.problem).toContain("b.arxml");
    expect(verdict.problem).toContain("2 files");
  });

  it("refuses the WHOLE batch when the job total breaks, keeping what was already staged", () => {
    // No single file is at fault, so accepting whichever prefix happens to fit would drop the tail
    // on a decision the person picking never made.
    const verdict = checkStaging({
      limits: shipped,
      staged: [file("first.arxml", 400 * MiB)],
      incoming: [file("second.arxml", 100 * MiB), file("third.arxml", 100 * MiB)],
    });

    expect(verdict.accepted).toEqual([]);
    expect(verdict.problem).toContain("600.0 MiB");
    expect(verdict.problem).toContain("512.0 MiB");
    expect(verdict.problem).toContain("Nothing was added");
  });

  it("counts the total across every file setting, not just this one", () => {
    // The ceiling the runtime enforces is job-wide; a per-setting check here would pass a job the
    // instance then refuses after the whole upload.
    const verdict = checkStaging({
      limits: shipped,
      elsewhere: [file("other-setting.arxml", 500 * MiB)],
      incoming: [file("mine.arxml", 100 * MiB)],
    });

    expect(verdict.accepted).toEqual([]);
    expect(verdict.problem).toContain("600.0 MiB");
  });

  it("refuses on the count even when every byte ceiling is satisfied", () => {
    // The hole the count closes: a great many tiny files satisfy both byte ceilings while producing
    // an absurd number of elements.
    const verdict = checkStaging({
      limits: { ...shipped, maxJobFiles: 4 },
      staged: many(3, 1),
      incoming: many(2, 1),
    });

    expect(verdict.accepted).toEqual([]);
    expect(verdict.problem).toContain("5 files");
    expect(verdict.problem).toContain("4 files one job may carry");
  });

  it("reports the count in the instance's terms and never as a byte size", () => {
    const verdict = checkStaging({
      limits: { ...shipped, maxJobFiles: 1 },
      incoming: many(2, 1),
    });

    expect(verdict.problem).not.toContain("B");
    expect(verdict.problem).toContain("2 files");
  });

  it("checks the count and the total against the survivors, not the raw pick", () => {
    // An oversized file is already refused, so it must not also push the total over and turn a
    // one-file problem into "nothing was added".
    const verdict = checkStaging({
      limits: { maxFileBytes: 10 * MiB, maxJobFileBytes: 30 * MiB, maxJobFiles: 256 },
      incoming: [file("ok.arxml", 5 * MiB), file("huge.arxml", 100 * MiB)],
    });

    expect(verdict.accepted.map((f) => f.name)).toEqual(["ok.arxml"]);
    expect(verdict.problem).toContain("huge.arxml");
    expect(verdict.problem).not.toContain("Nothing was added");
  });

  it("forecloses splitting a claimed set, and only for a claimed set", () => {
    const set = checkStaging({
      limits: shipped,
      incoming: many(6, 100 * MiB),
      claimedSet: true,
    });
    expect(set.problem).toContain("ONE set");
    expect(set.problem).toContain("withdraws");

    // A single-file setting cannot be split, so the warning would be noise there.
    const single = checkStaging({
      limits: shipped,
      staged: [file("held.arxml", 100 * MiB)],
      incoming: [file("a.arxml", 100 * MiB)],
      elsewhere: [file("elsewhere.arxml", 400 * MiB)],
    });
    expect(single.problem).toContain("Nothing was added");
    expect(single.problem).not.toContain("withdraws");
  });

  it("names no configuration key, because the binding number may be the proxy's", () => {
    const verdict = checkStaging({
      limits: shipped,
      incoming: [file("huge.arxml", 900 * MiB)],
    });

    expect(verdict.problem).not.toContain("MaxFileBytes");
    expect(verdict.problem).not.toContain("Fallen8:");
    expect(verdict.problem).toContain("this instance");
  });

  it("checks nothing and invents nothing when the limits are unknown", () => {
    // The bug this rule exists to prevent: Studio once carried a ceiling of its own, below the
    // instance's, and refused jobs the instance would have accepted.
    const verdict = checkStaging({
      limits: undefined,
      incoming: [file("enormous.arxml", 6 * 1024 * MiB)],
    });

    expect(verdict.accepted).toHaveLength(1);
    expect(verdict.problem).toBeNull();
  });

  it("treats each ceiling of zero or less as switched off, one at a time", () => {
    const noPerFile = checkStaging({
      limits: { ...shipped, maxFileBytes: 0 },
      incoming: [file("huge.arxml", 400 * MiB)],
    });
    expect(noPerFile.problem).toBeNull();

    const noTotal = checkStaging({
      limits: { ...shipped, maxJobFileBytes: -1 },
      staged: [file("a.arxml", 100 * MiB)],
      incoming: [file("b.arxml", 100 * MiB)],
    });
    expect(noTotal.problem).toBeNull();

    const noCount = checkStaging({
      limits: { ...shipped, maxJobFiles: 0 },
      incoming: many(1000, 1),
    });
    expect(noCount.accepted).toHaveLength(1000);
    expect(noCount.problem).toBeNull();
  });

  it("passes a set sitting exactly on each ceiling", () => {
    const verdict = checkStaging({
      limits: { maxFileBytes: 100, maxJobFileBytes: 200, maxJobFiles: 2 },
      incoming: [file("a", 100), file("b", 100)],
    });

    expect(verdict.accepted).toHaveLength(2);
    expect(verdict.problem).toBeNull();
  });

  it("says nothing about an empty pick", () => {
    const verdict = checkStaging({
      limits: { maxFileBytes: 100, maxJobFileBytes: 100, maxJobFiles: 1 },
      staged: [file("held", 100)],
      incoming: [],
    });

    expect(verdict.accepted).toEqual([]);
    expect(verdict.problem).toBeNull();
  });
});

describe("stating the ceilings up front", () => {
  it("reads the shipped defaults back in binary units", () => {
    expect(describeLimits(shipped)).toBe(
      "This instance accepts 128.0 MiB per file, 512.0 MiB per job, 256 files per job.",
    );
  });

  it("omits a ceiling that is switched off", () => {
    expect(describeLimits({ ...shipped, maxJobFiles: 0 })).toBe(
      "This instance accepts 128.0 MiB per file, 512.0 MiB per job.",
    );
  });

  it("says so when nothing is capped at all", () => {
    expect(describeLimits({ maxFileBytes: 0, maxJobFileBytes: 0, maxJobFiles: 0 })).toContain(
      "no ceiling",
    );
  });

  it("falls back to the unknown note rather than a number it does not have", () => {
    expect(describeLimits(undefined)).toBe(LIMITS_UNKNOWN_NOTE);
    expect(LIMITS_UNKNOWN_NOTE).toContain("nothing is checked");
  });
});
