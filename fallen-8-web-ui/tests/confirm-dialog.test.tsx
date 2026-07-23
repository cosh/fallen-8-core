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
