// MIT License
//
// index.ts
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
 * The whole-Studio export surface (feature studio-embeddable). What a host may import is
 * exactly what is re-exported here - the packaging phase turns this module into the library
 * entry point, so anything not on this list is internal. Spec:
 * features/done/studio-embeddable/spec.md.
 *
 * The canvas half of the surface is NOT repeated here: ./canvas owns it (and is published as
 * the package's "./canvas" subpath for hosts that want the graph without the app shell), so
 * the two entries cannot drift.
 */

export { mountStudio, F8Studio } from "../app/mount";
export type { StudioConfig } from "../app/studioConfig";
export type { InstanceAuth, InstanceConfig } from "../instances/types";

export * from "./canvas";
