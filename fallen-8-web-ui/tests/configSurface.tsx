// MIT License
//
// configSurface.tsx
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

import { expect } from "vitest";
import { screen } from "@testing-library/react";
import type { UserEvent } from "@testing-library/user-event";

/**
 * Opening the configuration surface, for the two suites that need to reach a settings row
 * (feature configuration-surface). Not a *.test.tsx file, so vitest does not collect it.
 */

/**
 * Clicks Configure and waits for the dialog. Asserts the button is enabled first, because a click on
 * a disabled button is silently a no-op and the failure would otherwise read as a timeout waiting for
 * the dialog rather than as "the config read never landed".
 */
export async function openConfig(user: UserEvent): Promise<HTMLElement> {
  const button = await screen.findByTestId("config-configure");
  expect(button, "Configure is disabled, so GET /config has not answered yet").not.toBeDisabled();
  await user.click(button);
  return screen.findByTestId("config-surface");
}

/** Navigates the surface's section nav. The pane shows one section at a time. */
export async function selectSection(user: UserEvent, id: string): Promise<void> {
  await user.click(await screen.findByTestId(`config-section-${id}`));
}
