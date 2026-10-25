'use client';
import { useEffect, useId, useState } from 'react';
import useSWR from 'swr';
import { toast } from 'sonner';
import { AlertCircle, Plus, X } from 'lucide-react';
import { api, fetcher, type Opportunity, type Me } from '@/lib/api';
import { Alert, AlertDescription } from './ui/alert';
import { Button } from './ui/button';
import { Checkbox } from './ui/checkbox';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from './ui/dialog';
import { Field, FieldDescription, FieldGroup, FieldLabel, FieldSet } from './ui/field';
import { Input } from './ui/input';
import { NativeSelect, NativeSelectOption } from './ui/native-select';
import { Separator } from './ui/separator';
import { ErrorBox } from './workspace-shared';
import {
  HospitalCommercialLink,
  money,
  operator,
  type Company,
  type Page,
  type Quote,
  type Service,
} from './commercial-shared';

function readQuote(value?: string): Quote | null {
  try {
    return value ? JSON.parse(value) : null;
  } catch {
    return null;
  }
}
export function OpportunityCommercial({
  opportunity,
  onChange,
}: {
  opportunity: Opportunity;
  onChange: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [companyQuery, setCompanyQuery] = useState('');
  const [selected, setSelected] = useState<{ serviceId: string; quantity: number }[]>([]);
  const [company, setCompany] = useState('');
  const [quote, setQuote] = useState<Quote | null>(readQuote(opportunity.hospitalQuote));
  const [dirty, setDirty] = useState(false);
  const [busy, setBusy] = useState(false);
  const [confirmed, setConfirmed] = useState(false);
  const [reference, setReference] = useState(opportunity.paymentReference ?? '');
  const { data: me } = useSWR<Me>('/me', fetcher);
  const fieldId = useId();
  const services = useSWR<Page<Service>>(
    open ? `/commercial/services?q=${encodeURIComponent(query)}` : null,
    fetcher,
  );
  const companies = useSWR<Page<Company>>(
    open ? `/commercial/companies?q=${encodeURIComponent(companyQuery)}` : null,
    fetcher,
  );
  useEffect(() => {
    const q = readQuote(opportunity.hospitalQuote);
    setQuote(q);
    setSelected(q?.lines.map((l) => ({ serviceId: l.serviceId, quantity: l.quantity })) ?? []);
    setCompany(q?.companyId ?? '');
    setDirty(false);
  }, [opportunity.hospitalQuote]);
  useEffect(() => {
    setReference(opportunity.paymentReference ?? '');
  }, [opportunity.paymentReference]);
  const frozen = busy || !!opportunity.hospitalPurchaseId || !!opportunity.purchasePending;
  async function quoteNow() {
    setBusy(true);
    try {
      const q = await api<Quote>(`/commercial/opportunities/${opportunity.id}/quote`, 'POST', {
        lines: selected,
        companyId: company || null,
      });
      setQuote(q);
      setDirty(false);
      onChange();
      toast.success('Cotización calculada por Hospital');
    } catch (e) {
      toast.error((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function purchase() {
    if (!quote) return;
    setBusy(true);
    try {
      await api(`/commercial/opportunities/${opportunity.id}/purchase`, 'POST', {
        quoteVersion: quote.quoteVersion,
        paymentReceived: confirmed,
        paymentReference: reference,
      });
      toast.success('Compra registrada en Hospital. Contacto convertido en cliente.');
      setOpen(false);
    } catch (e) {
      toast.error((e as Error).message);
    } finally {
      onChange();
      setBusy(false);
    }
  }
  return (
    <>
      <Button size="sm" variant="outline" onClick={() => setOpen(true)}>
        {opportunity.hospitalPurchaseId
          ? 'Ver compra Hospital'
          : opportunity.purchasePending
            ? 'Comprobar compra'
            : 'Servicios y cotización'}
      </Button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent className="max-h-dvh overflow-y-auto">
          <DialogHeader>
            <DialogTitle>Servicios · {opportunity.title}</DialogTitle>
            <DialogDescription>
              Precios y convenio calculados por Hospital. Marcar un seguimiento como ganado no
              registra un pago.
            </DialogDescription>
          </DialogHeader>
          <div className="flex min-w-0 flex-col gap-4">
            <div className="flex">
              <HospitalCommercialLink />
            </div>
            <ErrorBox error={services.error ?? companies.error} />
            {!opportunity.hospitalPurchaseId && (
              <FieldSet disabled={frozen || !operator(me)} className="min-w-0 gap-4">
                <FieldGroup className="gap-4">
                  <Field className="gap-2">
                    <FieldLabel htmlFor={`${fieldId}-query`}>Buscar servicio</FieldLabel>
                    <Input
                      id={`${fieldId}-query`}
                      value={query}
                      onChange={(e) => setQuery(e.target.value)}
                      placeholder="Nombre o código del hospital"
                    />
                  </Field>
                  <Field className="gap-2">
                    <FieldLabel htmlFor={`${fieldId}-service`}>Agregar servicio</FieldLabel>
                    <NativeSelect
                      id={`${fieldId}-service`}
                      value=""
                      onChange={(e) => {
                        if (
                          e.target.value &&
                          !selected.some((l) => l.serviceId === e.target.value)
                        ) {
                          setSelected([...selected, { serviceId: e.target.value, quantity: 1 }]);
                          setDirty(true);
                        }
                      }}
                    >
                      <NativeSelectOption value="">
                        Selecciona consulta o tratamiento
                      </NativeSelectOption>
                      {services.data?.items.map((s) => (
                        <NativeSelectOption key={s.id} value={s.id}>
                          {s.name} · {money(s.price, s.currency)}
                        </NativeSelectOption>
                      ))}
                    </NativeSelect>
                  </Field>
                  {selected.map((line, i) => (
                    <div className="flex min-w-0 items-center gap-2" key={line.serviceId}>
                      <span className="min-w-0 flex-1 truncate text-sm">
                        {services.data?.items.find((s) => s.id === line.serviceId)?.name ??
                          quote?.lines.find((l) => l.serviceId === line.serviceId)?.name ??
                          'Servicio seleccionado'}
                      </span>
                      <Input
                        className="w-20"
                        aria-label={`Cantidad servicio ${i + 1}`}
                        type="number"
                        min={1}
                        max={1000}
                        value={line.quantity}
                        onChange={(e) => {
                          setSelected(
                            selected.map((l) =>
                              l.serviceId === line.serviceId
                                ? { ...l, quantity: Number(e.target.value) }
                                : l,
                            ),
                          );
                          setDirty(true);
                        }}
                      />
                      <Button
                        type="button"
                        variant="ghost"
                        size="icon-sm"
                        aria-label={`Quitar servicio ${i + 1}`}
                        onClick={() => {
                          setSelected(selected.filter((l) => l.serviceId !== line.serviceId));
                          setDirty(true);
                        }}
                      >
                        <X />
                      </Button>
                    </div>
                  ))}
                  {!quote?.customerId && (
                    <>
                      <Field className="gap-2">
                        <FieldLabel htmlFor={`${fieldId}-company-query`}>Buscar empresa</FieldLabel>
                        <Input
                          id={`${fieldId}-company-query`}
                          value={companyQuery}
                          onChange={(e) => setCompanyQuery(e.target.value)}
                        />
                      </Field>
                      <Field className="gap-2">
                        <FieldLabel htmlFor={`${fieldId}-company`}>
                          Empresa para esta compra
                        </FieldLabel>
                        <NativeSelect
                          id={`${fieldId}-company`}
                          value={company}
                          onChange={(e) => {
                            setCompany(e.target.value);
                            setDirty(true);
                          }}
                        >
                          <NativeSelectOption value="">Sin empresa</NativeSelectOption>
                          {companies.data?.items
                            .filter((c) => c.active)
                            .map((c) => (
                              <NativeSelectOption key={c.id} value={c.id}>
                                {c.name}
                              </NativeSelectOption>
                            ))}
                          {company && !companies.data?.items.some((c) => c.id === company) && (
                            <NativeSelectOption value={company}>
                              Empresa de la cotización
                            </NativeSelectOption>
                          )}
                        </NativeSelect>
                      </Field>
                    </>
                  )}
                  <FieldDescription>
                    Si ya es cliente, Hospital aplica la empresa asignada a su ficha.
                  </FieldDescription>
                  <Button
                    className="self-start"
                    variant={quote && !dirty ? 'outline' : 'default'}
                    disabled={!selected.length || frozen || !operator(me)}
                    onClick={quoteNow}
                  >
                    <Plus />
                    Cotizar con Hospital
                  </Button>
                </FieldGroup>
              </FieldSet>
            )}
            {quote && (
              <>
                <Separator />
                <section className="flex flex-col gap-2 text-sm" aria-label="Cotización Hospital">
                  <h3 className="font-medium">
                    {opportunity.hospitalPurchaseId ? 'Compra pagada' : 'Cotización Hospital'}
                  </h3>
                  {quote.lines.map((l) => (
                    <p className="flex justify-between gap-4" key={l.serviceId}>
                      <span className="min-w-0">
                        {l.quantity} × {l.name}
                      </span>
                      <strong className="font-medium">{money(l.total, quote.currency)}</strong>
                    </p>
                  ))}
                  <p className="flex justify-between gap-4 text-muted-foreground">
                    <span>Subtotal</span>
                    <span>{money(quote.subtotal, quote.currency)}</span>
                  </p>
                  <p className="flex justify-between gap-4 text-muted-foreground">
                    <span>Convenio {quote.discountPercent}%</span>
                    <span>−{money(quote.discountAmount, quote.currency)}</span>
                  </p>
                  <Separator />
                  <p className="flex justify-between gap-4">
                    <strong className="font-semibold">Total</strong>
                    <strong className="font-semibold">{money(quote.total, quote.currency)}</strong>
                  </p>
                  {dirty && (
                    <p className="text-muted-foreground">
                      Recalcula la cotización para aplicar los cambios.
                    </p>
                  )}
                </section>
              </>
            )}
            {opportunity.purchasePending && (
              <Alert role="status">
                <AlertCircle />
                <AlertDescription>
                  Compra en comprobación. El reintento recupera el mismo registro de Hospital;
                  conserva su referencia de pago.
                </AlertDescription>
              </Alert>
            )}
            {quote && !opportunity.hospitalPurchaseId && operator(me) && (
              <>
                <Separator />
                <FieldGroup className="gap-4">
                  <Field className="gap-2">
                    <FieldLabel htmlFor={`${fieldId}-reference`}>Referencia del pago</FieldLabel>
                    <Input
                      id={`${fieldId}-reference`}
                      maxLength={120}
                      value={reference}
                      disabled={busy || opportunity.purchasePending}
                      onChange={(e) => setReference(e.target.value)}
                    />
                  </Field>
                  <Field orientation="horizontal" className="items-start">
                    <Checkbox
                      id={`${fieldId}-confirmed`}
                      checked={confirmed}
                      disabled={busy}
                      onCheckedChange={(value) => setConfirmed(value === true)}
                    />
                    <FieldLabel htmlFor={`${fieldId}-confirmed`} className="font-normal">
                      Confirmo que el pago ya fue recibido. Esta acción registra la compra y no
                      cobra dinero.
                    </FieldLabel>
                  </Field>
                </FieldGroup>
                <DialogFooter>
                  <Button disabled={!confirmed || busy || dirty} onClick={purchase}>
                    {busy
                      ? 'Comprobando Hospital…'
                      : opportunity.purchasePending
                        ? 'Recuperar compra registrada'
                        : 'Registrar compra pagada'}
                  </Button>
                </DialogFooter>
              </>
            )}
          </div>
        </DialogContent>
      </Dialog>
    </>
  );
}
