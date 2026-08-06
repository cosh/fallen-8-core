// MIT License
//
// ConfirmDialog.tsx
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

import { useState, type ReactNode } from "react";
import * as Dialog from "@radix-ui/react-dialog";
import { Field } from "./Field";
import { usePortalContainer } from "../app/studioConfig";

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
  const portalContainer = usePortalContainer();
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
      <Dialog.Portal container={portalContainer}>
        <Dialog.Overlay className="modal-overlay" />
        <Dialog.Content className="panel modal-center w-[28rem] max-w-[90vw] p-4">
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
