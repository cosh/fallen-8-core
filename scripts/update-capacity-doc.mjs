// MIT License
//
// update-capacity-doc.mjs
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

// Renders a fallen-8-bench capacity report into the Capacity and performance page.
//
// The page owns its prose; this script owns only the four generated regions, each delimited by
// <!-- capacity:<name> --> ... <!-- /capacity:<name> -->. Everything outside a region is left byte
// for byte alone, so a writer can edit the page freely without fighting the generator.
//
// Usage:  node scripts/update-capacity-doc.mjs [report.json] [--check]
//         --check exits non-zero when the page would change, for CI.

import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join, resolve } from "node:path";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const args = process.argv.slice(2);
const check = args.includes("--check");
const reportPath = resolve(root, args.find((a) => !a.startsWith("--")) ?? join("fallen-8-bench", "results", "capacity-report.json"));
const docPath = join(root, "docs", "src", "content", "docs", "capacity-and-performance.md");
const schemaPath = join(root, "fallen-8-bench", "capacity-report.schema.json");

const fail = (message) => {
  console.error("update-capacity-doc: " + message);
  process.exit(1);
};

// ---------------------------------------------------------------- validation
// A focused structural check against the shape capacity-report.schema.json declares. It is not a
// full JSON Schema engine: it enforces the parts this renderer depends on (the major version, the
// required objects, and a non-empty numeric row set per metric family) and names the offending path
// when something is off, so a bad report fails here rather than producing a silently wrong page.
const requireObject = (value, path) => {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    fail(`${path} must be an object`);
  }
  return value;
};

const requireRows = (value, path, fields) => {
  if (!Array.isArray(value) || value.length === 0) {
    fail(`${path} must be a non-empty array`);
  }
  value.forEach((row, i) => {
    requireObject(row, `${path}[${i}]`);
    for (const field of fields) {
      if (typeof row[field] !== "number" || !Number.isFinite(row[field])) {
        fail(`${path}[${i}].${field} must be a finite number`);
      }
    }
  });
  return value;
};

const schemaMajor = (() => {
  try {
    return JSON.parse(readFileSync(schemaPath, "utf8")).properties.schemaVersion.const;
  } catch {
    return "1";
  }
})();

let report;
try {
  report = JSON.parse(readFileSync(reportPath, "utf8"));
} catch (error) {
  fail(`cannot read ${reportPath}: ${error.message}`);
}

requireObject(report, "report");
if (report.schemaVersion !== schemaMajor) {
  fail(`schemaVersion ${JSON.stringify(report.schemaVersion)} is not the supported major ${JSON.stringify(schemaMajor)}`);
}
const env = requireObject(report.environment, "environment");
const source = requireObject(report.source, "source");
const metrics = requireObject(report.metrics, "metrics");
requireRows(metrics.memory, "metrics.memory", ["vertices", "edges", "bytesPerVertex", "bytesPerEdge", "retainedMb"]);
requireRows(metrics.writeThroughput, "metrics.writeThroughput", ["producers", "writes", "writesPerSecond"]);
requireRows(metrics.saveStall, "metrics.saveStall", ["elements", "writerHoldMs"]);
requireRows(metrics.traversal, "metrics.traversal", ["vertices", "edges", "iterations", "edgesPerSecond"]);

// ---------------------------------------------------------------- rendering
const int = (n) => Math.round(n).toLocaleString("en-US");
const one = (n) => n.toFixed(1);

const environmentBlock = () => {
  const lines = [
    `The numbers on this page come from one recorded run of that tool. They describe **that machine**:`,
    ``,
    `| | |`,
    `| --- | --- |`,
    `| Machine | ${env.runnerLabel ?? "unlabelled"} |`,
    `| CPU | ${env.processorName ?? "not reported"}, ${env.processorCount} logical processors |`,
    `| Memory | ${env.totalPhysicalMemoryMb ? int(env.totalPhysicalMemoryMb) + " MB available to the runtime" : "not reported"} |`,
    `| OS | ${env.operatingSystem} (${env.architecture}) |`,
    `| Runtime | ${env.runtime}, server GC ${env.serverGarbageCollection ? "on" : "off"} |`,
    `| Engine | ${source.engineVersion}${source.commit ? `, commit \`${source.commit.slice(0, 10)}\`` : ""}${source.dirtyWorkingTree ? " (uncommitted changes present)" : ""} |`,
    `| Profile | \`${report.profile}\` |`,
    `| Measured | ${new Date(report.generatedAtUtc).toISOString().replace("T", " ").slice(0, 16)} UTC |`,
  ];
  return lines.join("\n");
};

const memoryBlock = () => {
  const rows = metrics.memory.map(
    (m) =>
      `| ${int(m.vertices)} vertices, ${int(m.edges)} edges (avg degree ${int(m.averageDegree)}) | ${one(m.retainedMb)} MB | ${one(m.bytesPerVertex)} B | ${one(m.bytesPerEdge)} B |`
  );
  return [
    "| Graph | Retained | Per vertex | Per edge (adjacency included) |",
    "| --- | --- | --- | --- |",
    ...rows,
  ].join("\n");
};

const writeBlock = () => {
  // The write count is per row: each scenario runs under a time cap, so an fsync-bound serial run
  // commits fewer writes than a concurrent one in the same window. The rate is over what committed,
  // so showing the count is what makes the comparison checkable rather than merely plausible.
  const rows = metrics.writeThroughput.map(
    (w) => `| ${w.label} | ${int(w.writesPerSecond)} writes/s | ${int(w.writes)} |`
  );
  const serial = metrics.writeThroughput.find((w) => w.producers === 1);
  const best = metrics.writeThroughput.reduce((a, b) => (b.writesPerSecond > a.writesPerSecond ? b : a));
  const factor = serial && serial.writesPerSecond > 0 ? (best.writesPerSecond / serial.writesPerSecond).toFixed(1) : null;
  const out = ["| Producers | Throughput | Writes committed |", "| --- | --- | --- |", ...rows];
  if (factor && best !== serial) {
    out.push(
      "",
      `That is roughly ${factor}x from group commit alone, on single-element writes with the WAL on, and the serial latency floor is unchanged: a group of one still fsyncs immediately.`
    );
  }
  return out.join("\n");
};

const saveBlock = () => {
  const rows = metrics.saveStall.map(
    (s) => `| ${int(s.elements)} elements | ${one(s.writerHoldMs)} ms |`
  );
  return ["| Graph size | Save duration (writer held) |", "| --- | --- |", ...rows].join("\n");
};

const traversalBlock = () => {
  const rows = metrics.traversal.map(
    (t) =>
      `| ${int(t.vertices)} vertices, ${int(t.edges)} edges | ${int(t.iterations)} | ${int(t.edgesPerSecond)} edges/s |`
  );
  return ["| Graph | Passes | Out-edge traversal |", "| --- | --- | --- |", ...rows].join("\n");
};

const regions = {
  environment: environmentBlock(),
  memory: memoryBlock(),
  writes: writeBlock(),
  save: saveBlock(),
  traversal: traversalBlock(),
};

// ---------------------------------------------------------------- splice
let doc;
try {
  doc = readFileSync(docPath, "utf8");
} catch (error) {
  fail(`cannot read ${docPath}: ${error.message}`);
}

const original = doc;
for (const [name, body] of Object.entries(regions)) {
  const open = `<!-- capacity:${name} -->`;
  const close = `<!-- /capacity:${name} -->`;
  const start = doc.indexOf(open);
  const end = doc.indexOf(close);
  if (start === -1 || end === -1 || end < start) {
    fail(`the page is missing the ${open} ... ${close} region`);
  }
  doc = doc.slice(0, start + open.length) + "\n\n" + body + "\n\n" + doc.slice(end);
}

if (doc === original) {
  console.log("update-capacity-doc: page already matches " + reportPath);
  process.exit(0);
}

if (check) {
  console.error("update-capacity-doc: the page is out of date with " + reportPath);
  process.exit(1);
}

writeFileSync(docPath, doc);
console.log("update-capacity-doc: rewrote the generated regions of " + docPath);
