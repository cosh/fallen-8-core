// MIT License
//
// restartCopy.ts
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

// The ONE home for how Studio talks about a setting that needs a restart (feature
// writable-instance-config 5.7). Before this, the phrasing lived in four places and the namespaces
// panel said in a comment that it was borrowing the configuration view's register, which is the kind
// of copy that drifts apart one edit at a time.
//
// There is deliberately no restart BUTTON anywhere: a single-process self-hosted server has no
// supervisor contract to restart into, so Studio can only tell an operator what their own restart
// would apply.

/** Appended to a message about a change that only the next boot will act on. */
export const TAKES_EFFECT_ON_RESTART = "takes effect on restart";

/** The short chip beside a row whose stored value is not the value in force. */
export const RESTART_PENDING_CHIP = "restart to apply";

/**
 * The banner summary. Deliberately "differs from what this instance started with" rather than "you
 * changed this": appsettings.json reloads on change and nothing observes it, so this lights up when
 * an operator hand-edits that file too, and blaming the reader for someone else's edit is worse than
 * being vague.
 */
export function restartBannerSummary(count: number): string {
  return count === 1
    ? "1 setting differs from what this instance started with. Restart the server to apply it."
    : `${count} settings differ from what this instance started with. Restart the server to apply them.`;
}

