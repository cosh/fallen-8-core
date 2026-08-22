// MIT License
//
// configCatalog.ts
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

import type { ConfigSettingSource, SettingREST } from "../api/types";

/**
 * The client's view of the server's setting catalog (feature configuration-surface): how the
 * instance's settings are grouped for a reader, how a query and a filter select among them, and the
 * two per-key rules Studio derives from a descriptor.
 *
 * It exists because GET /config publishes a FLAT list of about a hundred descriptors in the server's
 * own declaration order, and one flat list is what made the old inline panel unreadable. Everything
 * here is pure: no React, no API call, so the grouping is testable on its own, which matters because
 * a key this module silently dropped would be editable nowhere and invisible in a screenshot.
 *
 * It deliberately carries NO description of what an individual key means. That lives on the server's
 * own catalog reason and on each feature's docs page; a second copy here would drift. The one thing
 * this module does explain is what a SECTION governs, in one line, because no server field says that.
 */

/** A nav heading over a run of sections. */
export type ConfigGroupId = "graph" | "workloads" | "semantic" | "operations" | "reference" | "ungrouped";

export const CONFIG_GROUPS: readonly { id: ConfigGroupId; label: string }[] = [
  { id: "graph", label: "graph" },
  { id: "workloads", label: "workloads" },
  { id: "semantic", label: "semantic" },
  { id: "operations", label: "operations" },
  { id: "reference", label: "read-only reference" },
  // Only rendered when a raw section this version does not map turns up, so the heading has to be
  // honest that Studio, not the operator, is behind: the keys under it may well be writable.
  { id: "ungrouped", label: "not grouped yet" },
];

export interface ConfigSection {
  id: string;
  group: ConfigGroupId;
  label: string;
  /** One line on what the section governs. Never what a single key does. */
  blurb: string;
  /**
   * The raw config sections (a key's SECOND segment) this entry collects, in render order. The first
   * is the primary: its own keys render without a sub-group header.
   */
  raw: readonly string[];
  /** True when the pane renders its rows without sub-group headers at all. */
  flat?: boolean;
}

/** Where an unmapped raw section lands, so a section this version does not know is still readable. */
export const OTHER_SECTION_ID = "other";

/**
 * The ordered sections. Not alphabetical and not the server's order: the read/write sections come
 * first in rough tuning order and the two entirely read-only ones last, because the reason to open
 * this surface is almost always to change something.
 *
 * `raw` is a section NAME, never a key list: the server encodes the grouping in the key itself, and a
 * key list would silently miss the next key added under a section.
 */
export const CONFIG_SECTIONS: readonly ConfigSection[] = [
  {
    id: "namespaces",
    group: "graph",
    label: "Namespaces",
    blurb: "How many graphs this instance allows, and which ones the next boot loads.",
    raw: ["Namespaces"],
    flat: true,
  },
  {
    id: "durability",
    group: "graph",
    label: "Storage and durability",
    // Metadata:Directory is merged in here: it is the same kind of on-disk path key, and its own
    // reason names the namespace inventory, the save-game registry and the stored settings file.
    blurb: "Where this instance keeps graphs, checkpoints, the write-ahead log and its own stored settings.",
    raw: ["Durability", "Metadata"],
  },
  {
    id: "changefeed",
    group: "workloads",
    label: "Change feed",
    blurb: "The live event stream: buffer depth, subscriber limits, keep-alive.",
    raw: ["ChangeFeed"],
    flat: true,
  },
  {
    id: "analytics",
    group: "workloads",
    label: "Analytics",
    blurb: "Time budgets and concurrency for algorithm runs.",
    raw: ["Analytics"],
    flat: true,
  },
  {
    id: "bulkio",
    group: "workloads",
    label: "Bulk import and export",
    blurb: "Batch size and the request and line limits on JSONL import.",
    raw: ["BulkIO"],
    flat: true,
  },
  {
    id: "ceilings",
    group: "workloads",
    label: "Registration ceilings",
    // Two sections of one key each, identical in shape and in reason: the server's own comment on the
    // stored-query ceiling is "for the same reason as the plugin ceiling".
    blurb: "How many runtime-authored plugins and stored queries may be registered. New registrations only.",
    raw: ["Plugins", "StoredQueries"],
    flat: true,
  },
  {
    id: "embedding",
    group: "semantic",
    label: "Embedding provider",
    blurb: "The vector provider, and the model identity stamped beside every vector already stored.",
    raw: ["Embedding"],
  },
  {
    id: "chat",
    group: "semantic",
    label: "Chat and language model",
    blurb: "The completion gateway behind POST /chat and the natural-language assist.",
    raw: ["Chat"],
  },
  {
    id: "ingestion",
    group: "semantic",
    label: "Document pipeline",
    // Nlp is merged in on the server's own account: Fallen8:Nlp:Enabled "gates no REST endpoint and
    // grants no caller anything. It only tells the ingestion pipeline whether to call the sidecar."
    blurb: "Upload limits, chunking, the indexes ingestion wires, and the two sidecars it calls.",
    raw: ["Ingestion", "Nlp"],
  },
  {
    id: "integrations",
    group: "operations",
    label: "Integrations runtime",
    blurb: "Whether an integrations runtime is reachable, and where.",
    raw: ["Integrations"],
    flat: true,
  },
  {
    id: "observability",
    group: "operations",
    label: "Observability",
    blurb: "What this instance exports right now, and the bounds on its graph-shape snapshot.",
    raw: ["Observability"],
  },
  {
    id: "identity",
    group: "reference",
    label: "Fleet identity",
    blurb: "The tenant and instance names stamped onto this process's telemetry. None of it is writable.",
    raw: ["Identity"],
  },
  {
    id: "security",
    group: "reference",
    label: "Security",
    blurb: "The API key, the perimeter and the rate limits. Nothing here is writable, by one blanket rule.",
    raw: ["Security"],
    flat: true,
  },
  {
    id: OTHER_SECTION_ID,
    group: "ungrouped",
    label: "Other",
    blurb: "Settings this instance publishes that this version of Studio does not group yet.",
    raw: [],
    flat: true,
  },
];

const SECTION_BY_RAW = new Map<string, ConfigSection>();
for (const section of CONFIG_SECTIONS) {
  for (const raw of section.raw) {
    SECTION_BY_RAW.set(raw, section);
  }
}

const OTHER_SECTION = CONFIG_SECTIONS.find((s) => s.id === OTHER_SECTION_ID)!;

/**
 * The section a key belongs to, from its SECOND segment, which is the same rule the server states for
 * itself. Exact-case on purpose: a mis-cased key is not the key the server publishes, and normalising
 * it here would hide that rather than showing it under "Other".
 */
export function sectionOf(key: string): ConfigSection {
  const parts = key.split(":");
  if (parts.length < 3 || parts[0] !== "Fallen8") {
    return OTHER_SECTION;
  }
  return SECTION_BY_RAW.get(parts[1]) ?? OTHER_SECTION;
}

/** A run of rows under one header inside a section pane. `label` is "" for the section's own keys. */
export interface SettingGroup {
  label: string;
  settings: SettingREST[];
}

export interface SectionGroup {
  section: ConfigSection;
  /** Every setting in the section, in render order. */
  settings: SettingREST[];
  /** The sub-groups, direct keys first; a single unlabelled group when the section renders flat. */
  groups: SettingGroup[];
}

/** The prefix a key shares with its siblings: Fallen8:Embedding:Onnx:ModelPath -> Fallen8:Embedding:Onnx. */
function groupKeyOf(key: string): string {
  const last = key.lastIndexOf(":");
  return last < 0 ? key : key.slice(0, last);
}

/** The two prefixes whose last segment reads badly as a header. Everything else uses its own name. */
const GROUP_LABELS: Readonly<Record<string, string>> = {
  "Fallen8:Nlp": "NLP sidecar",
  "Fallen8:Embedding:Onnx": "ONNX",
};

function groupLabelOf(groupKey: string, section: ConfigSection): string {
  const primary = section.raw[0];
  if (primary !== undefined && groupKey === `Fallen8:${primary}`) {
    return "";
  }
  return GROUP_LABELS[groupKey] ?? groupKey.slice(groupKey.lastIndexOf(":") + 1);
}

/**
 * Buckets the published settings into the ordered sections, preserving the server's order within a
 * raw section: the catalog authors related keys adjacently (an index switch is followed by the index
 * id it governs) and an alphabetical re-sort would split those pairs.
 *
 * Every input setting comes out in exactly one section. Nothing is filtered here, so a key this
 * version does not recognise lands in "Other" rather than disappearing.
 */
export function groupSettings(settings: readonly SettingREST[] | undefined): SectionGroup[] {
  const buckets = new Map<string, SettingREST[]>();
  for (const setting of settings ?? []) {
    const section = sectionOf(setting.key);
    const bucket = buckets.get(section.id);
    if (bucket) {
      bucket.push(setting);
    } else {
      buckets.set(section.id, [setting]);
    }
  }

  const result: SectionGroup[] = [];
  for (const section of CONFIG_SECTIONS) {
    const bucket = buckets.get(section.id);
    if (!bucket || bucket.length === 0) {
      continue;
    }
    const ordered = orderByRawSection(bucket, section);
    result.push({ section, settings: ordered, groups: subGroupsOf(ordered, section) });
  }
  return result;
}

/**
 * A merged section renders its raw sections in the declared order (Durability before Metadata), and
 * keeps the server's order inside each. The server happens to publish sections alphabetically, which
 * matches every merge this version declares, but relying on that would make the order accidental.
 */
function orderByRawSection(settings: readonly SettingREST[], section: ConfigSection): SettingREST[] {
  if (section.raw.length < 2) {
    return [...settings];
  }
  const rank = (setting: SettingREST) => {
    const index = section.raw.indexOf(setting.key.split(":")[1]);
    return index < 0 ? section.raw.length : index;
  };
  return settings
    .map((setting, index) => ({ setting, index }))
    .sort((a, b) => rank(a.setting) - rank(b.setting) || a.index - b.index)
    .map((entry) => entry.setting);
}

/**
 * Sub-groups from the key's own prefix, so a provider's keys sit together under its name and the
 * section's direct keys come first. First-appearance order for the rest, which is the server's.
 */
function subGroupsOf(settings: readonly SettingREST[], section: ConfigSection): SettingGroup[] {
  if (section.flat) {
    return [{ label: "", settings: [...settings] }];
  }
  const groups: SettingGroup[] = [];
  const byLabel = new Map<string, SettingGroup>();
  for (const setting of settings) {
    const label = groupLabelOf(groupKeyOf(setting.key), section);
    const existing = byLabel.get(label);
    if (existing) {
      existing.settings.push(setting);
      continue;
    }
    const group = { label, settings: [setting] };
    byLabel.set(label, group);
    groups.push(group);
  }
  // The unlabelled group is the section's own keys and reads as the introduction to the rest, so it
  // leads even when the server declares a provider's keys first.
  return [...groups.filter((g) => g.label === ""), ...groups.filter((g) => g.label !== "")];
}

/**
 * Whether a setting matches a search. Covers the key, the exclusion rule and the reason, and
 * deliberately NOT the value: a value here can be an endpoint or a filesystem path, and a value
 * search would turn the box into a way to fish for one.
 */
export function matchesQuery(setting: SettingREST, query: string): boolean {
  const needle = query.trim().toLowerCase();
  if (needle.length === 0) {
    return true;
  }
  return (
    setting.key.toLowerCase().includes(needle) ||
    (setting.rule ?? "").toLowerCase().includes(needle) ||
    (setting.reason ?? "").toLowerCase().includes(needle)
  );
}

/** The two sources a stored value can never outrank, so their rows are read-only. */
const AUTHORITY_SOURCES: readonly ConfigSettingSource[] = ["environment", "commandLine"];

/** The environment-variable spelling an operator has to remove to manage a key here instead. */
export function environmentSpelling(key: string): string {
  return key.replace(/:/g, "__");
}

export function isEnvironmentLocked(setting: SettingREST): boolean {
  return AUTHORITY_SOURCES.includes(setting.source);
}

/** A stable, key-derived handle: Fallen8:Plugins:MaxCount -> config-setting-fallen8-plugins-maxcount. */
export function settingTestId(key: string): string {
  return `config-setting-${key.replace(/[^a-z0-9]+/gi, "-").toLowerCase()}`;
}

export type ConfigFilterId =
  | "all"
  | "writable"
  | "restartPending"
  | "notWritable"
  | "setHere"
  | "environment";

/**
 * The filter strip. Each one answers a question an operator actually arrives with, and none of them
 * hides a row silently: the pane always states how many of how many it is showing.
 */
export const CONFIG_FILTERS: readonly { id: ConfigFilterId; label: string; title: string }[] = [
  { id: "all", label: "all", title: "Every setting in this section" },
  {
    id: "writable",
    label: "writable here",
    title: "Settings this surface can actually change: neither excluded by a rule nor declared in the environment",
  },
  {
    id: "restartPending",
    label: "restart to apply",
    title: "Settings whose stored value is not the value this process is running with",
  },
  { id: "notWritable", label: "not writable", title: "Settings excluded from writes by a rule, with the reason" },
  { id: "setHere", label: "set here", title: "Settings whose value comes from this instance's own stored configuration" },
  {
    id: "environment",
    label: "from the environment",
    title: "Settings a Fallen8__ variable or a command-line argument declares, which no stored value can outrank",
  },
];

export function matchesFilter(setting: SettingREST, filter: ConfigFilterId): boolean {
  switch (filter) {
    case "writable":
      return setting.tier !== "notWritable" && !isEnvironmentLocked(setting);
    // Keyed on the flag, never on an absent value: every never-writable descriptor omits its value,
    // so a value test would report fifty rows as pending.
    case "restartPending":
      return setting.restartPending === true;
    case "notWritable":
      return setting.tier === "notWritable";
    case "setHere":
      return setting.source === "override";
    case "environment":
      return isEnvironmentLocked(setting);
    case "all":
    default:
      return true;
  }
}
