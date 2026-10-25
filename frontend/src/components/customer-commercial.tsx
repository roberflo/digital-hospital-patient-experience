'use client';
import { useState } from 'react';
import useSWR, { useSWRConfig } from 'swr';
import { toast } from 'sonner';
import { RefreshCw } from 'lucide-react';
import { api, fetcher, type Contact, type Me } from '@/lib/api';
import { fallbackZone, inZone } from '@/lib/format';
import { Badge } from './ui/badge';
import { Button } from './ui/button';
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from './ui/dialog';
import { Item, ItemActions, ItemContent, ItemDescription, ItemGroup, ItemTitle } from './ui/item';
import { ErrorBox } from './workspace-shared';
import {
  HospitalCommercialLink,
  Pages,
  hint,
  money,
  operator,
  type Customer,
  type Page,
  type Purchase,
} from './commercial-shared';

export function CustomerCommercial({
  contact,
  onChange,
}: {
  contact: Contact;
  onChange: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [page, setPage] = useState(1);
  const [busy, setBusy] = useState(false);
  const { data: me } = useSWR<Me>('/me', fetcher);
  const { mutate: globalMutate } = useSWRConfig();
  const { data, error, mutate } = useSWR<{
    contact: Contact;
    customer?: Customer;
    candidates: Customer[];
    purchases?: Page<Purchase>;
  }>(open ? `/commercial/contacts/${contact.id}?page=${page}` : null, fetcher);
  async function act(path: string, body?: unknown) {
    setBusy(true);
    try {
      await api(`/commercial/contacts/${contact.id}/${path}`, 'POST', body);
      await mutate();
      await globalMutate(
        (k) =>
          typeof k === 'string' && (k.startsWith('/contacts') || k.startsWith('/opportunities')),
      );
      onChange();
      toast.success('Vínculo con Hospital actualizado');
    } catch (e) {
      toast.error((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <>
      <Button variant="outline" size="sm" onClick={() => setOpen(true)}>
        Cliente y compras
      </Button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent className="max-h-dvh overflow-y-auto">
          <DialogHeader>
            <DialogTitle>Cliente Hospital · {contact.name}</DialogTitle>
            <DialogDescription>
              Un contacto se convierte en cliente al comprar su primer servicio o al vincular un
              cliente existente de Hospital.
            </DialogDescription>
          </DialogHeader>
          <div className="flex min-w-0 flex-col items-start gap-4">
            <ErrorBox error={error} />
            <HospitalCommercialLink />
            {data && (
              <>
                <Badge variant={data.contact.isCustomer ? 'default' : 'secondary'}>
                  {data.contact.isCustomer ? 'Cliente vinculado' : 'Contacto sin compra vinculada'}
                </Badge>
                <p className="text-sm">
                  {data.customer?.companyName ?? 'Sin empresa asignada en Hospital'}
                </p>
                <Button
                  size="sm"
                  variant="outline"
                  disabled={busy || !operator(me)}
                  onClick={() => act('sync')}
                >
                  <RefreshCw />
                  Comprobar compras en Hospital
                </Button>
                {!data.contact.isCustomer && (
                  <div className="flex w-full min-w-0 flex-col gap-2">
                    <h3 className="text-sm font-medium">Clientes con el mismo teléfono</h3>
                    {data.candidates.length ? (
                      <ItemGroup className="gap-2">
                        {data.candidates.map((c) => (
                          <Item variant="outline" size="sm" role="listitem" key={c.id}>
                            <ItemContent className="min-w-0">
                              <ItemTitle>{c.name}</ItemTitle>
                              <ItemDescription>{c.companyName ?? 'Sin empresa'}</ItemDescription>
                            </ItemContent>
                            {operator(me) && (
                              <ItemActions>
                                <Button
                                  size="sm"
                                  disabled={busy}
                                  onClick={() => act('link', { customerId: c.id })}
                                >
                                  Vincular cliente
                                </Button>
                              </ItemActions>
                            )}
                          </Item>
                        ))}
                      </ItemGroup>
                    ) : (
                      <p className={hint}>
                        No hay clientes con este teléfono. Puedes vincular el expediente del
                        paciente o registrar su primera compra desde una oportunidad.
                      </p>
                    )}
                  </div>
                )}
                {data.purchases && (
                  <div className="flex w-full min-w-0 flex-col gap-2">
                    <h3 className="text-sm font-medium">Compras registradas en Hospital</h3>
                    {data.purchases.items.map((p) => (
                      <Item variant="outline" size="sm" asChild key={p.id}>
                        <article>
                          <ItemContent className="min-w-0">
                            <ItemTitle>{money(p.quote.total, p.quote.currency)}</ItemTitle>
                            <span className="text-xs text-muted-foreground">
                              {inZone(p.createdAt, me?.tenant.timeZone ?? fallbackZone, {
                                dateStyle: 'medium',
                                timeStyle: 'short',
                              })}
                            </span>
                            <ItemDescription className="line-clamp-none">
                              {p.quote.lines.map((l) => `${l.quantity} × ${l.name}`).join(', ')}
                            </ItemDescription>
                            <small className="text-xs text-muted-foreground">
                              Descuento: {p.quote.discountPercent}% ·{' '}
                              {p.status === 'completed' ? 'Pagada' : p.status}
                            </small>
                          </ItemContent>
                        </article>
                      </Item>
                    ))}
                    {!data.purchases.total && <p className={hint}>Sin compras registradas.</p>}
                    <Pages data={data.purchases} page={page} setPage={setPage} />
                  </div>
                )}
              </>
            )}
          </div>
        </DialogContent>
      </Dialog>
    </>
  );
}
