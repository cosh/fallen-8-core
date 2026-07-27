// MIT License
//
// NlBackendConfig.tsx
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

import { Field } from "../../components/Field";
import {
  DEFAULT_NL_MODEL,
  NL_PRESETS,
  usesApiKey,
  type NlAssistConfig,
} from "./config";

/**
 * The model-backend configuration form (nl-assist spec §4 / nl-assist-ux). Extracted so the
 * fragment editor (NlAssistPanel) and the whole-type plugin editor (PluginNlAssistPanel) share
 * ONE backend config UI over the same persisted store — the "one home per explanation" rule.
 * Presentational only: it renders the instance/custom switch, presets, endpoint/api/model/temp,
 * and the optional key, and reports changes back through {@link setConfig}.
 */
export function NlBackendConfig({
  config,
  setConfig,
}: {
  config: NlAssistConfig;
  setConfig: (patch: Partial<NlAssistConfig>) => void;
}) {
  return (
    <div className="space-y-2" data-testid="nl-config">
      <Field helpKey="nlBackend" label="backend" htmlFor="nl-mode">
        <select
          id="nl-mode"
          className="input w-auto"
          value={config.mode}
          onChange={(e) => setConfig({ mode: e.target.value as "instance" | "custom" })}
        >
          <option value="instance">this Fallen-8 instance</option>
          <option value="custom">custom endpoint (browser-direct)</option>
        </select>
      </Field>
      {config.mode === "instance" ? (
        <p className="text-fg-faint text-[10px]" data-testid="nl-instance-hint">
          Routed through the active instance: <code>POST /chat</code> proxies to the server's
          model backend (default <code>{DEFAULT_NL_MODEL}</code>, chosen on the server via
          Fallen8:Chat). Nothing to configure here; the prompt stays within the instance you
          are already connected to.
        </p>
      ) : (
        <>
          <Field helpKey="nlPreset" label="preset" htmlFor="nl-preset">
            <select
              id="nl-preset"
              className="input w-auto"
              value=""
              onChange={(e) => {
                const preset = NL_PRESETS.find((p) => p.name === e.target.value);
                if (preset) {
                  setConfig({
                    endpoint: preset.endpoint,
                    apiKind: preset.apiKind,
                    model: preset.model,
                  });
                }
              }}
            >
              <option value="">— prefill from preset —</option>
              {NL_PRESETS.map((preset) => (
                <option key={preset.name} value={preset.name}>
                  {preset.name}
                </option>
              ))}
            </select>
          </Field>
          <Field helpKey="nlEndpoint" label="endpoint" htmlFor="nl-endpoint">
            <input
              id="nl-endpoint"
              className="input"
              value={config.endpoint}
              onChange={(e) => setConfig({ endpoint: e.target.value })}
              placeholder="http://localhost:11434"
            />
          </Field>
          <div className="flex gap-2">
            <Field helpKey="nlApi" label="api" htmlFor="nl-kind">
              <select
                id="nl-kind"
                className="input w-auto"
                value={config.apiKind}
                onChange={(e) => setConfig({ apiKind: e.target.value as "ollama" | "openai" })}
              >
                <option value="ollama">ollama</option>
                <option value="openai">openai-compatible</option>
              </select>
            </Field>
            <Field helpKey="nlModel" label="model" htmlFor="nl-model" className="grow">
              <input
                id="nl-model"
                className="input"
                value={config.model}
                onChange={(e) => setConfig({ model: e.target.value })}
                placeholder="phi4-mini"
              />
            </Field>
            <Field helpKey="nlTemperature" label="temp" htmlFor="nl-temperature" className="w-16">
              <input
                id="nl-temperature"
                className="input"
                type="number"
                min="0"
                max="2"
                step="0.1"
                value={config.temperature}
                onChange={(e) => setConfig({ temperature: Number(e.target.value) || 0 })}
              />
            </Field>
          </div>
          {usesApiKey(config) ? (
            <Field
              helpKey="nlApiKey"
              label="api key (optional — sent only to the model endpoint)"
              htmlFor="nl-key"
            >
              <input
                id="nl-key"
                className="input"
                type="password"
                value={config.apiKey ?? ""}
                onChange={(e) => setConfig({ apiKey: e.target.value || undefined })}
              />
            </Field>
          ) : (
            <p className="text-fg-faint text-[10px]" data-testid="nl-no-key-hint">
              No API key — Ollama endpoints never use one.
            </p>
          )}
          <p className="text-fg-faint text-[10px]">
            Presets are prefills, not recommendations — the blessed setup stays the built-in MIT
            stack. Hosted endpoints must send CORS headers and show a “text leaves this machine”
            notice; for your own Ollama set <code>OLLAMA_ORIGINS</code> to this app's origin.
          </p>
        </>
      )}
    </div>
  );
}
