// MIT License
//
// samples-manifest.test.ts
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

import { existsSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { buildWindFarm } from "../scripts/samples/windFarm";
import type { SamplesManifest } from "../src/lib/samples";

/**
 * Guards the COMMITTED gallery manifest against the drift that would break a load at runtime
 * rather than at build time: a count that disagrees with its dataset, a linking allowlist
 * naming an index nobody creates, a seed pointing at a non-existent index, or a document asset
 * that is simply not on disk. Every check here maps to a way the wind-farm sample could
 * "load" and then quietly demo nothing.
 */

const SAMPLES_DIR = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "samples");
const manifest = JSON.parse(
  readFileSync(join(SAMPLES_DIR, "index.json"), "utf8"),
) as SamplesManifest;

describe("samples manifest", () => {
  it("is version 1 and non-empty", () => {
    expect(manifest.version).toBe(1);
    expect(manifest.samples.length).toBeGreaterThan(0);
  });

  it("every dataset file exists and its meta line matches the entry's counts", () => {
    for (const entry of manifest.samples) {
      const path = join(SAMPLES_DIR, entry.file);
      expect(existsSync(path), `${entry.id}: ${entry.file} is missing`).toBe(true);

      const firstLine = readFileSync(path, "utf8").split("\n", 1)[0];
      const meta = JSON.parse(firstLine) as {
        type: string;
        vertexCount: number;
        edgeCount: number;
      };
      expect(meta.type, `${entry.id}: first line is not the meta guard`).toBe("meta");
      expect(meta.vertexCount, `${entry.id}: vertexCount drift`).toBe(entry.vertexCount);
      expect(meta.edgeCount, `${entry.id}: edgeCount drift`).toBe(entry.edgeCount);
    }
  });

  it("every linking allowlist and seed names an index the same sample creates", () => {
    for (const entry of manifest.samples) {
      const created = new Set(entry.indexRecipes.map((r) => r.uniqueId));
      for (const indexId of entry.linkIndexIds ?? []) {
        expect(created.has(indexId), `${entry.id}: linkIndexIds names uncreated '${indexId}'`).toBe(
          true,
        );
      }
      for (const seed of entry.indexSeeds ?? []) {
        expect(
          created.has(seed.indexId),
          `${entry.id}: indexSeeds names uncreated '${seed.indexId}'`,
        ).toBe(true);
      }
    }
  });

  it("every declared document asset exists on disk", () => {
    for (const entry of manifest.samples) {
      for (const document of entry.documents ?? []) {
        expect(
          existsSync(join(SAMPLES_DIR, document.file)),
          `${entry.id}: document ${document.file} is missing`,
        ).toBe(true);
      }
    }
  });

  it("a sample that ingests documents also seeds an index, or its chunks link to nothing", () => {
    for (const entry of manifest.samples) {
      if (!entry.documents?.length || !entry.linkIndexIds?.length) continue;
      // Creation does not backfill, so linking without seeding silently finds no asset.
      const seeded = new Set((entry.indexSeeds ?? []).map((s) => s.indexId));
      for (const indexId of entry.linkIndexIds) {
        expect(
          seeded.has(indexId),
          `${entry.id}: links against '${indexId}' but never seeds it`,
        ).toBe(true);
      }
    }
  });

  it("text documents declare a format and binary ones do not", () => {
    for (const entry of manifest.samples) {
      for (const document of entry.documents ?? []) {
        if (document.kind === "text") {
          expect(document.format, `${entry.id}: ${document.file} needs a format`).toBeDefined();
        } else {
          expect(document.format, `${entry.id}: ${document.file} is binary`).toBeUndefined();
        }
      }
    }
  });
});

describe("the wind-farm knowledge sample", () => {
  const entry = manifest.samples.find((s) => s.id === "wind-farm");

  it("is registered in the gallery", () => {
    expect(entry).toBeDefined();
  });

  it("ships the three converter paths the demo claims: PDF, spreadsheet and plain markdown", () => {
    const files = (entry?.documents ?? []).map((d) => d.file);
    expect(files.some((f) => f.endsWith(".pdf"))).toBe(true);
    expect(files.some((f) => f.endsWith(".xlsx"))).toBe(true);
    expect(files.some((f) => f.endsWith(".md"))).toBe(true);
    // The markdown one must NOT need the docling sidecar.
    expect(entry?.documents?.find((d) => d.file.endsWith(".md"))?.kind).toBe("text");
  });

  it("carries the knowledge badge so the gallery filter can find it", () => {
    expect(entry?.badges).toContain("knowledge");
  });

  it("bakes no vectors: the embeddings it demonstrates are computed at ingest", () => {
    expect(entry?.embedding).toBeNull();
  });

  // The committed artifacts are produced by three SEPARATE steps: `npm run build:samples` writes
  // the jsonl and the manifest entry, a by-hand Python script writes the documents, and the docs
  // prose is written by a person. Nothing else checks that they were all regenerated together, so
  // a fleet-file edit could ship a manifest that claims something the corpus no longer says while
  // every other test still passed.
  it("matches what its generator produces right now (jsonl and manifest entry)", () => {
    const built = buildWindFarm();
    expect(built.entry).toEqual(entry);
    expect(built.jsonl).toEqual(readFileSync(join(SAMPLES_DIR, "wind-farm.jsonl"), "utf8"));
  });

  it("is deterministic: two builds are byte-identical", () => {
    expect(buildWindFarm().jsonl).toEqual(buildWindFarm().jsonl);
  });

  it("has documents that still name every person and vendor the fleet file declares", () => {
    // The markdown document is plain text, so it is greppable without a PDF or XLSX parser. If
    // the fleet file's names changed and the corpus was not regenerated, this fails.
    const fleet = JSON.parse(
      readFileSync(
        join(dirname(fileURLToPath(import.meta.url)), "..", "scripts", "samples", "data", "windFarmFleet.json"),
        "utf8",
      ),
    ) as { vendors: string[]; technicians: { name: string; signsDocuments: boolean }[] };
    const standard = readFileSync(join(SAMPLES_DIR, "documents", "nw-std-0417.md"), "utf8");

    for (const vendor of fleet.vendors) {
      expect(standard, `the standard no longer names vendor '${vendor}'`).toContain(vendor);
    }
    const signer = fleet.technicians.find((t) => t.signsDocuments);
    expect(standard, "the standard no longer names its approver").toContain(signer!.name);
  });

  it("names no real gearbox manufacturer, since the corpus alleges a casting defect", () => {
    // The documents blame a premature failure on a supplier's casting batch. Naming a real
    // company in that narrative is a claim about that company, so the vendors are invented.
    // generate-documents.py is included on purpose, and it is the important one: the defect this
    // guards against was a hardcoded "supplied by Vestas" literal in the generator's prose, which
    // lands in the PDF alone. Scanning only the shipped text assets would let that exact
    // regression through, since neither the PDF nor the XLSX is greppable from here.
    const corpus = [
      readFileSync(join(SAMPLES_DIR, "documents", "nw-std-0417.md"), "utf8"),
      readFileSync(join(SAMPLES_DIR, "documents", "generate-documents.py"), "utf8"),
      readFileSync(join(SAMPLES_DIR, "wind-farm.jsonl"), "utf8"),
      readFileSync(join(SAMPLES_DIR, "index.json"), "utf8"),
    ].join("\n");
    for (const brand of ["Vestas", "Siemens", "Gamesa", "Nordex", "Enercon", "Goldwind"]) {
      expect(corpus, `'${brand}' is a real manufacturer`).not.toContain(brand);
    }
  });

  it("names identifier-shaped asset tags in its dataset, or linking would find nothing", () => {
    const jsonl = readFileSync(join(SAMPLES_DIR, "wind-farm.jsonl"), "utf8");
    // The extractor needs an uppercase start, at least 4 characters and an underscore.
    const extractable = /"assetTag":\{"type":"System\.String","value":"([A-Z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+)"/g;
    const tags = [...jsonl.matchAll(extractable)].map((m) => m[1]);
    expect(tags.length).toBeGreaterThan(50);
    expect(tags.every((t) => t.length >= 4)).toBe(true);
    // The two tags the demo's payoff depends on.
    expect(tags).toContain("WTG_A17");
    expect(tags).toContain("GBX_BATCH_2023_11");
  });
});
