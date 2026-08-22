// MIT License
//
// ObservabilitySection.tsx
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

import type { ReactNode } from "react";
import type { ObservabilityConfigREST, SettingREST } from "../api/types";
import { environmentSpelling } from "../lib/configCatalog";
import { SettingRow } from "./SettingRow";

/**
 * The Observability section of the configuration surface (feature configuration-surface). It used to
 * be a dialog of its own, reached from a second Configure button on the Connect screen; folding it in
 * removed that button and, with it, a real duplication: three of these six keys were rendered twice on
 * Connect, once here as read-only rows and once as editable rows in the flat settings list.
 *
 * It is the ONE section with a hand-authored layout instead of the surface's generic key grouping,
 * because the three group hints below are the only in-product explanation of what these keys are for,
 * and the server's setting catalog is deliberately structured to carry no meaning. Push and Pull are
 * also not a key-prefix grouping: the sampling ratio has no Otlp segment and belongs to Push anyway.
 *
 * Two row shapes, for a reason. The exporter switches and the endpoint decide the security posture of
 * a metrics surface, so they are never writable and the server withholds their descriptor value; their
 * effective value arrives on the observability block instead, which is what EnvRow reads. The sampling
 * ratio and the two statistics bounds beside them ARE writable, so they render as ordinary setting rows.
 */

function EnvRow({ label, value, envKey }: { label: string; value: string; envKey: string }) {
  // Every row gets a stable handle derived from its label (the shape DelegateSlot.tsx already uses),
  // so a docs capture can assert that the row it photographs is actually configured without this
  // component growing one prop per screenshot. "OTLP endpoint" -> config-otlp-endpoint.
  const testId = `config-${label.replace(/[^a-z0-9]+/gi, "-").toLowerCase()}`;
  return (
    <div className="border-line grid grid-cols-[10rem_1fr] items-baseline gap-2 border-b py-1.5 text-[12px] last:border-b-0">
      <span className="text-fg-dim">{label}</span>
      <div className="min-w-0">
        <div className="text-fg wrap-break-word" data-testid={testId}>
          {value}
        </div>
        <code className="text-fg-faint text-[10px]">{envKey}</code>
      </div>
    </div>
  );
}

function ObsSection({ title, hint, children }: { title: string; hint: string; children: ReactNode }) {
  return (
    <div>
      <div className="text-accent text-[10px] font-bold tracking-widest uppercase">{title}</div>
      <p className="text-fg-faint mt-0.5 mb-1 text-[11px]">{hint}</p>
      <div>{children}</div>
    </div>
  );
}

/** The keys this layout places by hand. Anything else the instance publishes falls out below. */
const SAMPLING = "Fallen8:Observability:TracingSamplingRatio";
const ELEMENT_BUDGET = "Fallen8:Observability:StatisticsElementBudget";
const TOP_N = "Fallen8:Observability:StatisticsTopN";
const PLACED = new Set([
  "Fallen8:Observability:Otlp:Endpoint",
  "Fallen8:Observability:Prometheus:Enabled",
  "Fallen8:Observability:Prometheus:RequireApiKey",
  SAMPLING,
  ELEMENT_BUDGET,
  TOP_N,
]);

export interface ObservabilitySectionProps {
  observability: ObservabilityConfigREST;
  /** The section's descriptors. Empty on an instance that publishes no settings inventory. */
  settings: readonly SettingREST[];
  draft: Record<string, string | null>;
  onChange: (key: string, value: string) => void;
  onClear: (key: string) => void;
  isRowDisabled: (key: string) => boolean;
}

export function ObservabilitySection({
  observability,
  settings,
  draft,
  onChange,
  onClear,
  isRowDisabled,
}: ObservabilitySectionProps) {
  const byKey = new Map(settings.map((setting) => [setting.key, setting]));

  /**
   * The editable bound, or the read-only line this section showed before the settings inventory
   * existed. The fallback is not dead code: an older instance answers GET /config with no `settings`
   * at all, and losing the value entirely would be worse than showing it read-only.
   */
  function bound(key: string, label: string, fallback: string) {
    const setting = byKey.get(key);
    if (!setting) {
      return <EnvRow label={label} value={fallback} envKey={environmentSpelling(key)} />;
    }
    return (
      <SettingRow
        setting={setting}
        draft={draft[key]}
        disabled={isRowDisabled(key)}
        onChange={onChange}
        onClear={onClear}
      />
    );
  }

  const unplaced = settings.filter((setting) => !PLACED.has(setting.key));

  return (
    <div className="space-y-4" data-testid="config-observability-overlay">
      {/* Deliberately does NOT restate what the section governs: the section's own blurb above says
          that. What is left is the part only this section can say. */}
      <p className="text-fg-dim text-[12px]">
        The exporter switches and the endpoint are read-only wherever you look at them: each decides
        the security posture of a metrics surface, so they are set where the instance is deployed. The
        sampling ratio and the two statistics bounds beside them are writable here.
      </p>

      <ObsSection
        title="Push (OTLP)"
        hint="Metrics, traces, and logs pushed to a collector. This is the live path in the default environment."
      >
        <EnvRow
          label="OTLP endpoint"
          value={observability.otlpEnabled ? (observability.otlpEndpoint ?? "(set)") : "off"}
          envKey="Fallen8__Observability__Otlp__Endpoint"
        />
        {bound(SAMPLING, "trace sampling", observability.tracingSamplingRatio.toString())}
      </ObsSection>

      <ObsSection
        title="Pull (Prometheus scrape)"
        hint="An optional GET /metrics endpoint a Prometheus server scrapes. Off by default and independent of the push above; leave it off when pushing."
      >
        <EnvRow
          label="scrape endpoint"
          value={observability.prometheusEnabled ? "on (GET /metrics)" : "off"}
          envKey="Fallen8__Observability__Prometheus__Enabled"
        />
        <EnvRow
          label="requires API key"
          value={observability.prometheusRequireApiKey ? "yes" : "no"}
          envKey="Fallen8__Observability__Prometheus__RequireApiKey"
        />
      </ObsSection>

      <ObsSection
        title="Statistics snapshot"
        hint="Bounds for the on-demand GET /statistics graph-shape snapshot. Not an exporter."
      >
        {bound(ELEMENT_BUDGET, "element budget", observability.statisticsElementBudget.toLocaleString())}
        {bound(TOP_N, "top-N", observability.statisticsTopN.toString())}
      </ObsSection>

      {unplaced.length > 0 && (
        <ObsSection
          title="Also under Fallen8:Observability"
          hint="Published by this instance and not placed in a group above by this version of Studio."
        >
          {unplaced.map((setting) => (
            <SettingRow
              key={setting.key}
              setting={setting}
              draft={draft[setting.key]}
              disabled={isRowDisabled(setting.key)}
              onChange={onChange}
              onClear={onClear}
            />
          ))}
        </ObsSection>
      )}
    </div>
  );
}
