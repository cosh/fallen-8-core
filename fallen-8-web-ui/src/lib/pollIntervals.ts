// MIT License
//
// pollIntervals.ts
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

/**
 * The poll cadence shared by every observer of an instance's two cheap discovery routes:
 * `/status` (the connection probe, the durability banner, the Instances row health cell) and
 * `/ns` (the namespace inventory, read by the switcher, NamespacesPanel and the namespace-recover
 * screen). All of them key their query on `[instanceId, "status"]` or `[instanceId, "namespaces"]`,
 * and TanStack times `refetchInterval` PER OBSERVER: two observers of one key with two different
 * intervals do not give each consumer its own poll rate, they refresh the shared row on the union
 * of both cadences, so neither consumer gets the interval it declared and the shorter one sets the
 * floor for both. One constant removes the possibility.
 *
 * Tune the number here, not per call site.
 */
export const STATUS_POLL_MS = 15_000;
