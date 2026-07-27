// MIT License
//
// confirm-dialog.test.tsx
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

import { describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import { ConfirmDialog } from "../src/components/ConfirmDialog";

/**
 * The typed-name gate is deliberate friction, not a credential. The prompt renders the
 * instance name uppercased (the .label style), so the confirmation must accept what the
 * user reads and types back regardless of case — matching case-sensitively rejected the
 * "LOCAL" a user typed after seeing TYPE "LOCAL".
 */
describe("ConfirmDialog typed-name gate", () => {
  const base = {
    open: true,
    title: "Replace the current graph",
    description: "Erased and replaced.",
    instanceName: "local",
    endpoint: "http://localhost:5000 (same origin)",
    confirmLabel: "Erase and load",
    onCancel: () => {},
  };

  const type = (value: string) =>
    fireEvent.change(screen.getByTestId("confirm-typed"), { target: { value } });

  it("arms on the uppercased form the prompt displays", () => {
    const onConfirm = vi.fn();
    render(<ConfirmDialog {...base} onConfirm={onConfirm} />);

    expect(screen.getByTestId("confirm-action")).toBeDisabled();
    type("LOCAL");
    expect(screen.getByTestId("confirm-action")).toBeEnabled();
    fireEvent.click(screen.getByTestId("confirm-action"));
    expect(onConfirm).toHaveBeenCalledTimes(1);
  });

  it("arms on the exact name and a padded variant, but not on a different name", () => {
    render(<ConfirmDialog {...base} onConfirm={() => {}} />);
    const action = screen.getByTestId("confirm-action");

    type("  Local ");
    expect(action).toBeEnabled();

    type("remote");
    expect(action).toBeDisabled();
  });
});
