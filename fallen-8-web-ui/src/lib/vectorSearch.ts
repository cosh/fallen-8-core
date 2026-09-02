// MIT License
//
// vectorSearch.ts
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
 * The engine's own ceiling on a kNN `k`, mirrored client-side.
 *
 * ONE home, because every caller of `/scan/index/vector` and `/embedding/search` has to agree with
 * the server or the request is refused outright: `VectorIndex.MaxK` is 1024 and
 * `VectorIndex.TryNearestNeighbors` rejects anything above it, which the embedding controller
 * turns into a 400 AFTER the provider has already embedded the query text. So a k picked from some
 * other quantity does not degrade, it fails, and it fails having spent a model call.
 *
 * That is not hypothetical: the canvas Interact tab first shipped this asking for the canvas
 * element cap (20,000) worth of neighbours, which made its semantic filter unusable on every real
 * instance while every mocked test passed.
 */
export const MAX_K = 1024;

/**
 * A kNN window that cannot exceed the engine's ceiling, for a caller who wants "as many as I might
 * need" rather than a number a person typed. `wanted` is what the caller would ideally ask for.
 */
export function boundedK(wanted: number): number {
  return Math.max(1, Math.min(Math.floor(wanted), MAX_K));
}
