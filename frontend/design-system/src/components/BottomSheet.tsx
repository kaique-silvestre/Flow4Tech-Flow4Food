import * as Dialog from "@radix-ui/react-dialog";
import type { ReactNode } from "react";

export interface BottomSheetProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  /** One sentence under the title (where the action goes, what can be undone). */
  description?: string | undefined;
  children?: ReactNode;
  /** Buttons at the bottom; the primary one goes last. */
  footer?: ReactNode;
  /** A failed request: inline message with the sheet kept open so nothing is lost. */
  error?: string | undefined;
  /** A request is in flight: the sheet announces it as busy. */
  loading?: boolean;
}

/** Short tasks over the list (new comanda, send the round, close the bill). Radix Dialog gives the focus trap, Escape and the labelled dialog role. */
export function BottomSheet({ open, onOpenChange, title, description, children, footer, error, loading = false }: BottomSheetProps) {
  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="f4-scrim" />
        <Dialog.Content className="f4-sheet" aria-busy={loading || undefined} {...(description ? {} : { "aria-describedby": undefined })}>
          <Dialog.Title className="f4-sheet-title">{title}</Dialog.Title>
          {description ? <Dialog.Description className="f4-sheet-desc">{description}</Dialog.Description> : null}
          {children}
          {error ? (
            <p role="alert" className="f4-field-error">
              {error}
            </p>
          ) : null}
          {footer ? <div className="f4-sheet-footer">{footer}</div> : null}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
