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
 * The host-facing export surface (feature studio-embeddable). What a host may import is
 * exactly what is re-exported here - the packaging phase turns this module into the library
 * entry point, so anything not on this list is internal. Spec:
 * features/open/studio-embeddable/spec.md.
 */

export { mountStudio, F8Studio } from "../app/mount";
export type { StudioConfig, ThemeTokens } from "../app/studioConfig";
export type { InstanceAuth, InstanceConfig } from "../instances/types";

export { F8GraphCanvas, type F8GraphCanvasProps } from "./F8GraphCanvas";
export type { ElementRef } from "../canvas/GraphCanvas";
export { DEFAULT_STYLE_CONFIG } from "../canvas/styleConfig";
export type { StyleConfig } from "../canvas/styleConfig";
export type { CanvasEdge, CanvasNode } from "../state/instanceStore";
export type { PathREST } from "../api/types";
