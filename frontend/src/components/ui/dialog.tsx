'use client';
import * as React from 'react';
import * as D from '@radix-ui/react-dialog';
import { X } from 'lucide-react';
import { cn } from '@/lib/utils';
export const Dialog = D.Root;
export const DialogTrigger = D.Trigger;
// Callers that pass no className get the product's dialog heading, so every
// dialog opens with the same title, description and spacing.
export function DialogTitle({ className, ...props }: React.ComponentProps<typeof D.Title>) {
  return <D.Title className={className ?? 'dialog-title'} {...props} />;
}
export function DialogDescription({
  className,
  ...props
}: React.ComponentProps<typeof D.Description>) {
  return <D.Description className={className ?? 'dialog-description'} {...props} />;
}
export function DialogContent({
  children,
  className,
  showClose = true,
  ...props
}: React.ComponentProps<typeof D.Content> & { showClose?: boolean }) {
  return (
    <D.Portal>
      <D.Overlay className="fixed inset-0 z-50 bg-slate-950/35 backdrop-blur-[2px]" />
      <D.Content
        className={cn(
          'fixed left-1/2 top-1/2 z-50 max-h-[90dvh] w-[calc(100%-2rem)] max-w-lg -translate-x-1/2 -translate-y-1/2 overflow-auto rounded-2xl border bg-white p-6 shadow-2xl',
          className,
        )}
        {...props}
      >
        {children}
        {showClose && (
          <D.Close
            className="absolute right-4 top-4 rounded p-1 hover:bg-muted"
            aria-label="Cerrar"
          >
            <X size={18} />
          </D.Close>
        )}
      </D.Content>
    </D.Portal>
  );
}
