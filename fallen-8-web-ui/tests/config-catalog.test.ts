// MIT License
//
// config-catalog.test.ts
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
import type { SettingREST } from "../src/api/types";
import {
  CONFIG_FILTERS,
  CONFIG_GROUPS,
  CONFIG_SECTIONS,
  OTHER_SECTION_ID,
  environmentSpelling,
  groupSettings,
  isEnvironmentLocked,
  matchesFilter,
  matchesQuery,
  sectionOf,
  settingTestId,
} from "../src/lib/configCatalog";
import { SHIPPED_SETTING_KEYS } from "./shippedSettingKeys";

/**
 * The grouping behind the configuration surface (feature configuration-surface).
 *
 * The property worth protecting above all others: a key the grouping DROPS is editable nowhere and
 * invisible everywhere. It would not show up in a screenshot, in a single-row query, or in any
 * component test that looks for the rows it does expect. So the shape of this suite is "account for
 * every key", not "check a few".
 */

function setting(key: string, overrides: Partial<SettingREST> = {}): SettingREST {
  return {
    key,
    kind: "string",
    tier: "restart",
    applyMode: "restart",
    value: "x",
    source: "default",
    restartPending: false,
    ...overrides,
  };
}

const shipped = SHIPPED_SETTING_KEYS.map((key) => setting(key));

describe("configuration section taxonomy", () => {
  it("accounts for every shipped key exactly once", () => {
    const grouped = groupSettings(shipped);
    const placed = grouped.flatMap((entry) => entry.settings.map((s) => s.key));

    expect(placed).toHaveLength(SHIPPED_SETTING_KEYS.length);
    expect(new Set(placed).size).toBe(SHIPPED_SETTING_KEYS.length);
    expect([...placed].sort()).toEqual([...SHIPPED_SETTING_KEYS].sort());
  });

  it("puts every shipped key in a mapped section, never in Other", () => {
    // Other is the visible fallback for a section this version does not know. A shipped key landing
    // there means the taxonomy went stale against the server, which is the failure this pins.
    const stray = SHIPPED_SETTING_KEYS.filter((key) => sectionOf(key).id === OTHER_SECTION_ID);
    expect(stray).toEqual([]);
  });

  it("groups the shipped keys into the counts the spec declares", () => {
    const counts = Object.fromEntries(
      groupSettings(shipped).map((entry) => [entry.section.id, entry.settings.length]),
    );

    expect(counts).toEqual({
      namespaces: 3,
      durability: 6, // Durability 5 + Metadata 1
      changefeed: 5,
      analytics: 3,
      bulkio: 3,
      ceilings: 2, // Plugins 1 + StoredQueries 1
      embedding: 21,
      chat: 9,
      ingestion: 29, // Ingestion 23 + Nlp 6
      integrations: 3,
      observability: 6,
      identity: 4,
      security: 8,
    });
    // Every declared section except the Other fallback is populated by a real instance.
    expect(Object.keys(counts)).toHaveLength(CONFIG_SECTIONS.length - 1);
  });

  it("renders the sections in the declared order and never invents one", () => {
    const grouped = groupSettings(shipped);
    const declared = CONFIG_SECTIONS.map((s) => s.id).filter((id) =>
      grouped.some((entry) => entry.section.id === id),
    );
    expect(grouped.map((entry) => entry.section.id)).toEqual(declared);
  });

  it("gives every section a group heading that exists, and every heading a section", () => {
    const headings = new Set(CONFIG_GROUPS.map((g) => g.id));
    for (const section of CONFIG_SECTIONS) {
      expect(headings, `section ${section.id}`).toContain(section.group);
    }
    for (const group of CONFIG_GROUPS) {
      expect(
        CONFIG_SECTIONS.some((s) => s.group === group.id),
        `heading ${group.id} has no section`,
      ).toBe(true);
    }
  });

  it("gives every section a distinct id, a label and a blurb", () => {
    expect(new Set(CONFIG_SECTIONS.map((s) => s.id)).size).toBe(CONFIG_SECTIONS.length);
    for (const section of CONFIG_SECTIONS) {
      expect(section.label.length, section.id).toBeGreaterThan(0);
      expect(section.blurb.length, section.id).toBeGreaterThan(0);
    }
  });

  it("claims each raw config section for exactly one entry", () => {
    const claimed = CONFIG_SECTIONS.flatMap((s) => s.raw);
    expect(new Set(claimed).size).toBe(claimed.length);
  });
});

describe("sectionOf", () => {
  it("reads the second segment, exactly as the server does", () => {
    expect(sectionOf("Fallen8:Analytics:MaxConcurrentRuns").id).toBe("analytics");
    expect(sectionOf("Fallen8:Embedding:Onnx:ModelPath").id).toBe("embedding");
  });

  it("falls back to Other for a section this version does not map, rather than dropping it", () => {
    const section = sectionOf("Fallen8:Quantum:Entanglement");
    expect(section.id).toBe(OTHER_SECTION_ID);

    // And the fallback is RENDERED: an unmapped key must reach a pane.
    const grouped = groupSettings([setting("Fallen8:Quantum:Entanglement")]);
    expect(grouped).toHaveLength(1);
    expect(grouped[0].section.id).toBe(OTHER_SECTION_ID);
    expect(grouped[0].settings.map((s) => s.key)).toEqual(["Fallen8:Quantum:Entanglement"]);
  });

  it("survives a key with too few segments, and a key under a foreign prefix", () => {
    expect(sectionOf("Fallen8:Analytics").id).toBe(OTHER_SECTION_ID);
    expect(sectionOf("Analytics").id).toBe(OTHER_SECTION_ID);
    expect(sectionOf("").id).toBe(OTHER_SECTION_ID);
    // Not Fallen8's key at all: mapping it onto the Analytics pane would be a guess.
    expect(sectionOf("Other:Analytics:MaxConcurrentRuns").id).toBe(OTHER_SECTION_ID);
  });

  it("is exact-case, so a mis-cased key is shown as unknown rather than silently normalised", () => {
    expect(sectionOf("fallen8:security:apikey").id).toBe(OTHER_SECTION_ID);
    expect(sectionOf("Fallen8:security:ApiKey").id).toBe(OTHER_SECTION_ID);
  });
});

describe("groupSettings", () => {
  it("returns nothing for an instance that publishes no settings at all", () => {
    // The older-server case: ConfigREST.settings is optional, and the pane must read it defensively.
    expect(groupSettings(undefined)).toEqual([]);
    expect(groupSettings([])).toEqual([]);
  });

  it("preserves the server's order inside a section", () => {
    // The catalog authors an index switch next to the index id it governs; an alphabetical re-sort
    // here would split those pairs and the pane would read as noise.
    const ingestion = groupSettings(shipped).find((entry) => entry.section.id === "ingestion")!;
    const keys = ingestion.settings.map((s) => s.key);
    const serverOrder = SHIPPED_SETTING_KEYS.filter(
      (key) => key.startsWith("Fallen8:Ingestion:") || key.startsWith("Fallen8:Nlp:"),
    );
    expect(keys).toEqual(serverOrder);
  });

  it("renders a merged section's raw sections in the declared order, not the arrival order", () => {
    const grouped = groupSettings([
      setting("Fallen8:Metadata:Directory"),
      setting("Fallen8:Durability:Volatile"),
    ]);
    expect(grouped[0].settings.map((s) => s.key)).toEqual([
      "Fallen8:Durability:Volatile",
      "Fallen8:Metadata:Directory",
    ]);
  });

  it("puts a section's own keys first, then one sub-group per provider prefix", () => {
    const chat = groupSettings(shipped).find((entry) => entry.section.id === "chat")!;
    expect(chat.groups.map((g) => ({ label: g.label, n: g.settings.length }))).toEqual([
      { label: "", n: 4 },
      { label: "Ollama", n: 2 },
      { label: "Nahil", n: 3 },
    ]);

    const embedding = groupSettings(shipped).find((entry) => entry.section.id === "embedding")!;
    expect(embedding.groups.map((g) => ({ label: g.label, n: g.settings.length }))).toEqual([
      { label: "", n: 10 },
      { label: "ONNX", n: 5 },
      { label: "LLamaSharp", n: 1 },
      { label: "Ollama", n: 2 },
      { label: "Nahil", n: 3 },
    ]);

    // A merged raw section becomes a sub-group under its own name.
    const ingestion = groupSettings(shipped).find((entry) => entry.section.id === "ingestion")!;
    expect(ingestion.groups.map((g) => ({ label: g.label, n: g.settings.length }))).toEqual([
      { label: "", n: 17 },
      { label: "Docling", n: 6 },
      { label: "NLP sidecar", n: 6 },
    ]);

    const durability = groupSettings(shipped).find((entry) => entry.section.id === "durability")!;
    expect(durability.groups.map((g) => ({ label: g.label, n: g.settings.length }))).toEqual([
      { label: "", n: 5 },
      { label: "Metadata", n: 1 },
    ]);
  });

  it("leads with the section's own keys even when the server declares a provider's first", () => {
    const grouped = groupSettings([
      setting("Fallen8:Embedding:Onnx:ModelPath"),
      setting("Fallen8:Embedding:Enabled"),
    ]);
    expect(grouped[0].groups.map((g) => g.label)).toEqual(["", "ONNX"]);
  });

  it("renders a flat section as one unlabelled group, headers and all suppressed", () => {
    const security = groupSettings(shipped).find((entry) => entry.section.id === "security")!;
    expect(security.groups).toHaveLength(1);
    expect(security.groups[0].label).toBe("");
    expect(security.groups[0].settings).toHaveLength(8);

    // Ceilings merges two one-key sections and would otherwise show one header for one row.
    const ceilings = groupSettings(shipped).find((entry) => entry.section.id === "ceilings")!;
    expect(ceilings.groups).toHaveLength(1);
    expect(ceilings.groups[0].settings.map((s) => s.key)).toEqual([
      "Fallen8:Plugins:MaxCount",
      "Fallen8:StoredQueries:MaxCount",
    ]);
  });

  it("keeps a section's sub-groups accounting for all of its settings", () => {
    for (const entry of groupSettings(shipped)) {
      const inGroups = entry.groups.flatMap((g) => g.settings.map((s) => s.key));
      expect(inGroups.sort(), entry.section.id).toEqual(entry.settings.map((s) => s.key).sort());
    }
  });

  it("groups an identity section that has no direct keys at all", () => {
    // Every Identity key is four segments, so there is no unlabelled group to lead with.
    const identity = groupSettings(shipped).find((entry) => entry.section.id === "identity")!;
    expect(identity.groups.map((g) => ({ label: g.label, n: g.settings.length }))).toEqual([
      { label: "Tenant", n: 2 },
      { label: "Instance", n: 2 },
    ]);
  });
});

describe("matchesQuery", () => {
  const row = setting("Fallen8:Security:ApiKey", {
    tier: "notWritable",
    rule: "R1",
    reason: "Blanking it locks every caller out with no way back in over REST.",
    value: undefined,
  });

  it("matches the key, the rule and the reason, case-insensitively", () => {
    expect(matchesQuery(row, "apikey")).toBe(true);
    expect(matchesQuery(row, "SECURITY")).toBe(true);
    expect(matchesQuery(row, "r1")).toBe(true);
    expect(matchesQuery(row, "locks every caller")).toBe(true);
  });

  it("never matches the value, so the box cannot be used to fish for an endpoint or a path", () => {
    const endpoint = setting("Fallen8:Chat:Ollama:Endpoint", { value: "http://ollama:11434" });
    expect(matchesQuery(endpoint, "11434")).toBe(false);
    expect(matchesQuery(endpoint, "ollama")).toBe(true); // via the key, not the value
  });

  it("treats an empty or whitespace query as no filter at all", () => {
    expect(matchesQuery(row, "")).toBe(true);
    expect(matchesQuery(row, "   ")).toBe(true);
  });

  it("does not match across the boundary between two fields", () => {
    // Concatenating key + rule + reason into one haystack would make this spuriously true.
    expect(matchesQuery(row, "apikey r1")).toBe(false);
  });

  it("survives a descriptor with no rule and no reason", () => {
    const plain = setting("Fallen8:Plugins:MaxCount", { rule: null, reason: null });
    expect(matchesQuery(plain, "maxcount")).toBe(true);
    expect(matchesQuery(plain, "nothing")).toBe(false);
  });
});

describe("matchesFilter", () => {
  it("counts a row as writable only when no rule and no environment variable stops it", () => {
    expect(matchesFilter(setting("Fallen8:Plugins:MaxCount"), "writable")).toBe(true);
    expect(
      matchesFilter(setting("Fallen8:Security:ApiKey", { tier: "notWritable" }), "writable"),
    ).toBe(false);
    // Writable by tier, but a variable outranks anything stored, so this surface cannot change it.
    expect(
      matchesFilter(setting("Fallen8:Plugins:MaxCount", { source: "environment" }), "writable"),
    ).toBe(false);
    expect(
      matchesFilter(setting("Fallen8:Plugins:MaxCount", { source: "commandLine" }), "writable"),
    ).toBe(false);
  });

  it("keys never-writable on the tier, not on an absent value", () => {
    // Every never-writable descriptor omits its value, so a value test would report fifty rows.
    const withheld = setting("Fallen8:Security:ApiKey", {
      tier: "notWritable",
      value: undefined,
      valueWithheld: true,
    });
    expect(matchesFilter(withheld, "notWritable")).toBe(true);
    // A writable key with no configured value is NOT in this bucket.
    const unset = setting("Fallen8:BulkIO:MaxImportRequestBytes", { value: null });
    expect(matchesFilter(unset, "notWritable")).toBe(false);
  });

  it("separates a stored value from one the environment declares", () => {
    expect(matchesFilter(setting("k:a:b", { source: "override" }), "setHere")).toBe(true);
    expect(matchesFilter(setting("k:a:b", { source: "appSettings" }), "setHere")).toBe(false);
    expect(matchesFilter(setting("k:a:b", { source: "environment" }), "environment")).toBe(true);
    expect(matchesFilter(setting("k:a:b", { source: "commandLine" }), "environment")).toBe(true);
    expect(matchesFilter(setting("k:a:b", { source: "override" }), "environment")).toBe(false);
  });

  it("reads restart-pending off the flag the server publishes", () => {
    expect(matchesFilter(setting("k:a:b", { restartPending: true }), "restartPending")).toBe(true);
    expect(matchesFilter(setting("k:a:b", { restartPending: false }), "restartPending")).toBe(false);
  });

  it("lets everything through under all, including a never-writable row", () => {
    expect(matchesFilter(setting("k:a:b", { tier: "notWritable" }), "all")).toBe(true);
  });

  it("declares a distinct id and a hover title for every filter", () => {
    expect(new Set(CONFIG_FILTERS.map((f) => f.id)).size).toBe(CONFIG_FILTERS.length);
    for (const filter of CONFIG_FILTERS) {
      expect(filter.title.length, filter.id).toBeGreaterThan(0);
    }
    expect(CONFIG_FILTERS[0].id).toBe("all");
  });

  it("partitions the shipped inventory: writable and not writable cover it exactly", () => {
    const writable = shipped.filter((s) => matchesFilter(s, "writable"));
    const not = shipped.filter((s) => matchesFilter(s, "notWritable"));
    // Nothing is in both, and with no environment in play the two halves are the whole.
    expect(writable.some((s) => not.includes(s))).toBe(false);
    expect(writable.length + not.length).toBe(shipped.length);
  });
});

describe("per-key rules Studio derives", () => {
  it("spells an environment variable the way an operator has to type it", () => {
    expect(environmentSpelling("Fallen8:Embedding:Onnx:ModelPath")).toBe(
      "Fallen8__Embedding__Onnx__ModelPath",
    );
  });

  it("locks exactly the two sources a stored value can never outrank", () => {
    const sources = ["default", "appSettings", "userSecrets", "environment", "commandLine", "host", "override"] as const;
    const locked = sources.filter((source) => isEnvironmentLocked(setting("k:a:b", { source })));
    expect(locked).toEqual(["environment", "commandLine"]);
  });

  it("derives a stable, collision-free handle for every shipped key", () => {
    expect(settingTestId("Fallen8:Plugins:MaxCount")).toBe("config-setting-fallen8-plugins-maxcount");
    const ids = SHIPPED_SETTING_KEYS.map(settingTestId);
    expect(new Set(ids).size).toBe(ids.length);
  });
});
