import { useState, type ReactNode } from "react";
import * as Dialog from "@radix-ui/react-dialog";
import { Field } from "./Field";

/**
 * Typed confirmation for destructive actions (FR-1d, FR-3): the dialog names the target
 * instance AND its endpoint, and the user must type the instance name to arm the action.
 * Optional `extra` content renders above the typed-name field (e.g. a "delete files too"
 * checkbox).
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
  extra,
}: {
  open: boolean;
  title: string;
  description: string;
  instanceName: string;
  endpoint: string;
  confirmLabel: string;
  onConfirm: () => void;
  onCancel: () => void;
  extra?: ReactNode;
}) {
  const [typed, setTyped] = useState("");
  // Case-insensitive, trimmed: the typed-name gate is deliberate friction, not a
  // credential, and the prompt renders the name uppercased (the .label style) — matching
  // exactly would reject the "LOCAL" a user types after reading "TYPE 'LOCAL'".
  const armed = typed.trim().toLowerCase() === instanceName.trim().toLowerCase();

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
          <Dialog.Title className="text-danger wrap-break-word text-sm font-bold">{title}</Dialog.Title>
          <Dialog.Description className="text-fg-dim mt-2 text-[12px]">
            {description} This targets{" "}
            <strong className="text-fg">{instanceName}</strong>{" "}
            (<span className="break-all">{endpoint}</span>).
          </Dialog.Description>
          {extra && <div className="mt-3">{extra}</div>}
          <Field
            helpKey="confirmTyped"
            label={
              <>
                type “<span className="normal-case">{instanceName}</span>” to confirm
              </>
            }
            htmlFor="confirm-typed"
            className="mt-4"
          >
            <input
              id="confirm-typed"
              data-testid="confirm-typed"
              className="input"
              value={typed}
              onChange={(e) => setTyped(e.target.value)}
              autoFocus
            />
          </Field>
          <div className="mt-4 flex justify-end gap-2">
            {/* Reset here too: a parent-driven close (open=false) never fires onOpenChange,
                and a surviving typed name would pre-arm the NEXT delete target. */}
            <button
              type="button"
              className="btn"
              onClick={() => {
                setTyped("");
                onCancel();
              }}
            >
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
