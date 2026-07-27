// MIT License
//
// AnalyticsScreen.tsx
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

import { AnalyticsRunner } from "../components/AnalyticsRunner";
import { GraphShapePanel } from "../components/GraphShapePanel";

/**
 * Analytics (feature studio-coverage §3/§4): understand the graph's shape, then compute
 * over it. The Graph shape panel is the ONLY caller of GET /statistics (on demand — the
 * pass is budgeted and rate-limited); its snapshot doubles as the schema cache feeding
 * identifier suggestions across the Studio (gap G-3). The runner mirrors the backend's
 * one-shot design: no history, no queueing — 429/408 are first-class outcomes.
 */
export function AnalyticsScreen() {
  return (
    <div className="mx-auto max-w-5xl space-y-4">
      <GraphShapePanel />
      <AnalyticsRunner />
    </div>
  );
}
