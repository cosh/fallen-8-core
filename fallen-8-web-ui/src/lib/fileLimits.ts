// MIT License
//
// fileLimits.ts
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

import type { FileLimits } from "../api/types";
import { formatBytes } from "./format";

/**
 * Whether a job's files fit what THIS instance accepts, checked at pick time (feature
 * integration-file-transport).
 *
 * This module is the only place in Studio allowed to reason about file ceilings, and it holds no
 * numbers of its own. Every ceiling arrives from `GET /integrations/limits` already reconciled with
 * the proxy's transport bound, so there is one number per question and nothing to combine. That
 * matters because the version this replaced had a ceiling of its own, about 384 MiB, which sat
 * BELOW the instance's: jobs the instance would have accepted were refused in the browser, and no
 * amount of configuration could fix it.
 *
 * The corollary is the rule for the unknown case. An instance too old to serve the route, or one
 * whose integrations capability is off, leaves the limits undefined, and then NOTHING is checked
 * and nothing is guessed: the send goes ahead and the instance refuses it if it must. A default
 * substituted here would be the same bug again.
 */

/** Enough of a file to check it, whichever transport carries it later. */
export interface SizedFile {
  name: string;
  size: number;
}

/** A ceiling of zero or less is switched off, as the runtime's options document. */
function inForce(ceiling: number | undefined): boolean {
  return typeof ceiling === "number" && Number.isFinite(ceiling) && ceiling > 0;
}

/**
 * The ceilings, or their absence. Absence has three spellings and they all mean the same thing
 * here: the query has not answered yet, it failed, or the instance answered with no body.
 */
export type MaybeLimits = FileLimits | null | undefined;

/**
 * What one file setting is being asked to hold, and what the rest of the job already holds.
 *
 * Generic over the incoming type, so a caller passing real `File` handles gets `File`s back with no
 * cast. That is not cosmetic: it is what makes "this only ever filters, it never constructs" a fact
 * the compiler holds rather than a comment somebody has to keep true.
 */
export interface StagingRequest<T extends SizedFile = SizedFile> {
  /** The ceilings this instance published, or absent when they could not be read. */
  limits: MaybeLimits;
  /** The files being added to ONE file setting, in the order they were picked. */
  incoming: T[];
  /** What that setting already holds. Kept whatever this verdict says. */
  staged?: SizedFile[];
  /** What every OTHER file setting of the same job holds: the total and the count are job-wide. */
  elsewhere?: SizedFile[];
  /** True when the setting declares `multiple`, so its files are read as ONE claimed set. */
  claimedSet?: boolean;
}

export interface StagingVerdict<T extends SizedFile = SizedFile> {
  /** The incoming files that may be staged, in pick order. */
  accepted: T[];
  /** One message for the setting's problem channel, or null when there is nothing to say. */
  problem: string | null;
}

/**
 * The refusal that keeps a set out of the tab before it is ever sent.
 *
 * Granularity differs between the three ceilings on purpose. A file over the per-file ceiling is
 * individually too big, so it is refused individually and its siblings still stage. A broken TOTAL
 * or COUNT is a property of the whole job, so no single file is at fault and none of the incoming
 * batch is accepted: picking some arbitrary prefix that fits would drop the tail on a decision the
 * person picking never made, and for a claimed set it would silently split the set.
 */
export function checkStaging<T extends SizedFile>(request: StagingRequest<T>): StagingVerdict<T> {
  const { limits, incoming } = request;
  const staged = request.staged ?? [];
  const elsewhere = request.elsewhere ?? [];

  if (!limits) return { accepted: incoming, problem: null };

  const oversized: T[] = [];
  const accepted: T[] = [];
  for (const file of incoming) {
    if (inForce(limits.maxFileBytes) && file.size > limits.maxFileBytes) oversized.push(file);
    else accepted.push(file);
  }

  if (accepted.length > 0) {
    const held = [...staged, ...elsewhere];
    const count = held.length + accepted.length;
    if (inForce(limits.maxJobFiles) && count > limits.maxJobFiles) {
      const ceiling = limits.maxJobFiles;
      return {
        accepted: [],
        problem: join(
          // Always plural on the left: this only fires when the count exceeds a ceiling of at
          // least one. The ceiling itself can legitimately be one file.
          `That would be ${count} files in this job, more than the ${ceiling} ` +
            `${ceiling === 1 ? "file" : "files"} one job may carry on this instance. ` +
            `Nothing was added.`,
          splitWarning(request),
        ),
      };
    }

    const total = bytesOf(held) + bytesOf(accepted);
    if (inForce(limits.maxJobFileBytes) && total > limits.maxJobFileBytes) {
      return {
        accepted: [],
        problem: join(
          `That would be ${formatBytes(total)} of files in this job, more than the ` +
            `${formatBytes(limits.maxJobFileBytes)} one job may carry on this instance. ` +
            `Nothing was added.`,
          splitWarning(request),
        ),
      };
    }
  }

  return { accepted, problem: oversized.length > 0 ? tooLargeMessage(oversized, limits) : null };
}

function bytesOf(files: SizedFile[]): number {
  return files.reduce((sum, file) => sum + file.size, 0);
}

function join(...parts: string[]): string {
  return parts.filter((part) => part.length > 0).join(" ");
}

/**
 * Forecloses the workaround the total and count refusals otherwise invite. Splitting a claimed set
 * over two runs is not slower, it is destructive: the second run's snapshot says it saw the whole
 * source, so it withdraws every element only the first run's files described.
 */
function splitWarning(request: StagingRequest): string {
  if (!request.claimedSet) return "";
  return (
    "These files are read as ONE set, so sending them in more than one run withdraws whatever " +
    "only the missing files described. Narrow the set instead of splitting it."
  );
}

function tooLargeMessage(oversized: SizedFile[], limits: FileLimits): string {
  const ceiling = `more than the ${formatBytes(limits.maxFileBytes)} one file may carry on this instance`;
  if (oversized.length === 1) {
    return `${oversized[0].name} is ${formatBytes(oversized[0].size)}, ${ceiling}.`;
  }
  const named = oversized.map((file) => `${file.name} (${formatBytes(file.size)})`).join(", ");
  return `${oversized.length} files are ${ceiling}: ${named}.`;
}

/**
 * What the form says when it could not read the ceilings. Its job is to stop someone reading the
 * absence of refusals as approval, without naming a number Studio does not know.
 */
export const LIMITS_UNKNOWN_NOTE =
  "This instance did not report what a job may carry, so nothing is checked here before the send. " +
  "Files that are too large are refused by the instance instead.";

/** The ceilings in one line, for the form to state up front rather than only when refusing. */
export function describeLimits(limits: MaybeLimits): string {
  if (!limits) return LIMITS_UNKNOWN_NOTE;
  const parts = [
    inForce(limits.maxFileBytes) ? `${formatBytes(limits.maxFileBytes)} per file` : "",
    inForce(limits.maxJobFileBytes) ? `${formatBytes(limits.maxJobFileBytes)} per job` : "",
    inForce(limits.maxJobFiles) ? `${limits.maxJobFiles} files per job` : "",
  ].filter((part) => part.length > 0);
  return parts.length === 0
    ? "This instance sets no ceiling on what a job may carry."
    : `This instance accepts ${parts.join(", ")}.`;
}
