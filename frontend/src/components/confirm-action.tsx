'use client';
import type { ReactNode } from 'react';
import { Slot } from 'radix-ui';
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogTrigger,
} from './ui/alert-dialog';

/** Asks before an action that is hard to take back. `ask={false}` runs it straight away. */
export function ConfirmAction({
  title,
  description,
  action,
  destructive = false,
  ask = true,
  onConfirm,
  children,
}: {
  title: string;
  description: ReactNode;
  action: string;
  destructive?: boolean;
  ask?: boolean;
  onConfirm: () => void;
  children: ReactNode;
}) {
  if (!ask) return <Slot.Root onClick={onConfirm}>{children}</Slot.Root>;
  return (
    <AlertDialog>
      <AlertDialogTrigger asChild>{children}</AlertDialogTrigger>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>{title}</AlertDialogTitle>
          <AlertDialogDescription>{description}</AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>Cancelar</AlertDialogCancel>
          <AlertDialogAction variant={destructive ? 'destructive' : 'default'} onClick={onConfirm}>
            {action}
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
