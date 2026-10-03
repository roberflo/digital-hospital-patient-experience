'use client';
import * as React from 'react';
import * as D from '@radix-ui/react-dialog';
import {X} from 'lucide-react';
import {cn} from '@/lib/utils';
export const Dialog=D.Root;export const DialogTrigger=D.Trigger;export const DialogTitle=D.Title;export const DialogDescription=D.Description;
export function DialogContent({children,className,...props}:React.ComponentProps<typeof D.Content>){return <D.Portal><D.Overlay className="fixed inset-0 z-50 bg-slate-950/35 backdrop-blur-[2px]"/><D.Content className={cn('fixed left-1/2 top-1/2 z-50 max-h-[90dvh] w-[calc(100%-2rem)] max-w-lg -translate-x-1/2 -translate-y-1/2 overflow-auto rounded-2xl border bg-white p-6 shadow-2xl',className)} {...props}>{children}<D.Close className="absolute right-4 top-4 rounded p-1 hover:bg-muted" aria-label="Cerrar"><X size={18}/></D.Close></D.Content></D.Portal>;}
