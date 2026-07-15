import { useState } from "react";
import * as Dialog from "@radix-ui/react-dialog";

/**
 * Typed confirmation for destructive actions (FR-1d, FR-3): the dialog names the target
 * instance AND its endpoint, and the user must type the instance name to arm the action.
 */
export function ConfirmDialog({
  open,
  title,
  description,
  instanceName,
  endpoint,
  confirmLabel,
  onConfirm,
  onCancel,
}: {
  open: boolean;
  title: string;
  description: string;
  instanceName: string;
  endpoint: string;
  confirmLabel: string;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const [typed, setTyped] = useState("");
  const armed = typed === instanceName;

  return (
    <Dialog.Root
      open={open}
      onOpenChange={(o) => {
        if (!o) {
          setTyped("");
          onCancel();
        }
      }}
    >
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-black/60" />
        <Dialog.Content className="panel fixed top-1/2 left-1/2 w-[28rem] max-w-[90vw] -translate-x-1/2 -translate-y-1/2 p-4">
          <Dialog.Title className="text-danger text-sm font-bold">{title}</Dialog.Title>
          <Dialog.Description className="text-fg-dim mt-2 text-[12px]">
            {description} This targets{" "}
            <strong className="text-fg">{instanceName}</strong>{" "}
            (<span className="break-all">{endpoint}</span>).
          </Dialog.Description>
          <label className="label mt-4" htmlFor="confirm-typed">
            type “{instanceName}” to confirm
          </label>
          <input
            id="confirm-typed"
            data-testid="confirm-typed"
            className="input"
            value={typed}
            onChange={(e) => setTyped(e.target.value)}
            autoFocus
          />
          <div className="mt-4 flex justify-end gap-2">
            <button type="button" className="btn" onClick={() => onCancel()}>
              Cancel
            </button>
            <button
              type="button"
              data-testid="confirm-action"
              className="btn btn-danger"
              disabled={!armed}
              onClick={() => {
                setTyped("");
                onConfirm();
              }}
            >
              {confirmLabel}
            </button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
