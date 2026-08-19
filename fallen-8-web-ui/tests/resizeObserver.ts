// MIT License
//
// resizeObserver.ts
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
 * jsdom implements no ResizeObserver, and both canvas renderers observe their container on
 * mount (Canvas2D to re-frame after a container-only reflow, Canvas3D to resize its scene).
 * This is the ONE fake: setup.ts installs it for every test so no constructor throws, and a
 * test that wants the observed BEHAVIOUR calls resizeObserved() to fire the live observers the
 * way a browser would after a reflow.
 */

type Observer = { callback: ResizeObserverCallback; targets: Set<Element> };

const observers = new Set<Observer>();

export class FakeResizeObserver implements ResizeObserver {
  private readonly self: Observer;

  constructor(callback: ResizeObserverCallback) {
    this.self = { callback, targets: new Set() };
    observers.add(this.self);
  }

  observe(target: Element): void {
    this.self.targets.add(target);
  }

  unobserve(target: Element): void {
    this.self.targets.delete(target);
  }

  disconnect(): void {
    this.self.targets.clear();
    observers.delete(this.self);
  }
}

/**
 * Fire every observer that still has a target, with an entry per target. The entries carry only
 * the target: nothing under test reads the contentRect (both renderers re-measure the element
 * themselves), and inventing box numbers would imply a fidelity this fake does not have.
 */
export function resizeObserved(): void {
  for (const observer of observers) {
    if (observer.targets.size === 0) continue;
    const entries = [...observer.targets].map((target) => ({ target }) as ResizeObserverEntry);
    observer.callback(entries, observer as unknown as ResizeObserver);
  }
}

/** How many observers are live, so a test can pin that teardown disconnected them. */
export function liveResizeObservers(): number {
  return observers.size;
}

/** Called from the global afterEach: a leaked observer would fire inside an unrelated test. */
export function resetResizeObservers(): void {
  observers.clear();
}
