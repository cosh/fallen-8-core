// MIT License
//
// applyStudioConfig.ts
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

import { setStudioConfig, storageKey, type StudioConfig } from "./studioConfig";
import { applyStudioConfigToRegistry } from "../instances/registry";
import { useNlAssist } from "../delegate/nl/config";
import { useFirstRun } from "../firstrun/firstRunStore";
import { dropMemoizedWorkspaceStores } from "../state/instanceStore";

/**
 * Make a mount's config the active one BEFORE anything renders (feature studio-embeddable).
 *
 * The three module-level persisted stores are re-pointed at the (possibly prefixed) storage
 * keys and hydrated - their first and only hydration, since they skip the import-time one -
 * which also injects the host's managed instances and namespace pin via the registry's
 * merge. Each of those merges derives its persisted fields from storage plus config alone,
 * so a mount that switches `storageNamespace` starts clean instead of inheriting the
 * previous mount's state. The per-instance workspace stores bake their key in at creation,
 * so the previous mount's memoized ones are dropped here rather than handed to this one.
 *
 * localStorage is synchronous, so all of this state is in place when the first render reads
 * it. Idempotent on purpose: StrictMode double-invokes the initializer that calls it.
 *
 * Lives outside mount.tsx so it is exercisable (and testable) without the React shell.
 */
export function applyStudioConfig(config: StudioConfig): void {
  setStudioConfig(config);
  dropMemoizedWorkspaceStores();
  void applyStudioConfigToRegistry();
  useNlAssist.persist.setOptions({ name: storageKey("f8.nl-assist") });
  void useNlAssist.persist.rehydrate();
  useFirstRun.persist.setOptions({ name: storageKey("f8.first-run") });
  void useFirstRun.persist.rehydrate();
}
