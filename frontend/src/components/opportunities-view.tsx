'use client';
import { useState } from 'react';
import useSWR from 'swr';
import { Plus, UserRound } from 'lucide-react';
import { toast } from 'sonner';
import { Badge } from './ui/badge';
import { Button } from './ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from './ui/card';
import { Field as UiField } from './ui/field';
import { NativeSelect, NativeSelectOption } from './ui/native-select';
import { api, fetcher, type Contact, type Opportunity } from '@/lib/api';
import { OpportunityCommercial } from './commercial-workspace';
import { ErrorBox } from './workspace-shared';
import { stages } from './conversation-workspace';
import { FormDialog } from './form-dialog';
async function contactNames(ids: string[]) {
  const pages = [];
  for (let i = 0; i < ids.length; i += 100)
    pages.push(
      api<Contact[]>(
        '/contacts?' +
          ids
            .slice(i, i + 100)
            .map((id) => 'id=' + id)
            .join('&'),
      ),
    );
  return new Map((await Promise.all(pages)).flat().map((c) => [c.id, c.name]));
}
export function OpportunitiesView({ search }: { search: string }) {
  const { data, error, mutate } = useSWR<Opportunity[]>('/opportunities', fetcher);
  const ids = [...new Set(data?.map((o) => o.contactId))].sort();
  const { data: names } = useSWR(ids.length ? 'contact-names:' + ids.join(',') : null, () =>
    contactNames(ids),
  );
  // The stage the new follow-up starts in; null while the dialog is closed.
  const [create, setCreate] = useState<string | null>(null);
  return (
    <>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <span className="text-sm text-muted-foreground">
          Seguimientos de la atención del hospital
        </span>
        <Button size="sm" onClick={() => setCreate('new')}>
          <Plus />
          Nuevo seguimiento
        </Button>
      </div>
      <ErrorBox error={error} />
      <div className="flex w-0 min-w-full items-start gap-4 overflow-x-auto pb-2">
        {stages.map(([id, label]) => {
          const rows = data?.filter(
            (o) => o.stage === id && o.title.toLowerCase().includes(search.toLowerCase()),
          );
          return (
            <section
              className="kanban-column flex max-w-80 min-w-64 flex-1 flex-col gap-3 rounded-xl bg-muted/50 p-3"
              key={id}
            >
              <div className="flex items-center gap-2">
                <h3 className="text-sm font-medium">{label}</h3>
                <Badge variant="secondary">{rows?.length ?? 0}</Badge>
              </div>
              {rows?.map((o) => (
                <Card key={o.id}>
                  <CardHeader>
                    <CardDescription>ATENCIÓN AL PACIENTE</CardDescription>
                    <CardTitle>
                      <h4 className="break-words">{o.title}</h4>
                    </CardTitle>
                  </CardHeader>
                  <CardContent className="flex flex-col gap-4">
                    <div className="flex items-center gap-2 text-sm text-muted-foreground">
                      <UserRound className="size-4 shrink-0" />
                      {names?.get(o.contactId) ?? 'Contacto'}
                    </div>
                    {o.value > 0 && (
                      <strong className="flex flex-wrap items-baseline gap-2 font-semibold">
                        ${o.value.toFixed(2)}{' '}
                        <small className="text-xs font-normal text-muted-foreground">
                          {o.hospitalPurchaseId
                            ? 'Pagado'
                            : o.hospitalQuote
                              ? 'Cotizado por Hospital'
                              : 'Estimado'}
                        </small>
                      </strong>
                    )}
                    <OpportunityCommercial
                      opportunity={o}
                      onChange={() => {
                        mutate();
                      }}
                    />
                    <UiField>
                      <NativeSelect
                        aria-label={'Etapa de ' + o.title}
                        value={o.stage}
                        onChange={async (e) => {
                          try {
                            await api(`/opportunities/${o.id}`, 'PATCH', { stage: e.target.value });
                            mutate();
                          } catch (err) {
                            toast.error((err as Error).message);
                          }
                        }}
                      >
                        {stages.map(([key, value]) => (
                          <NativeSelectOption value={key} key={key}>
                            {value}
                          </NativeSelectOption>
                        ))}
                      </NativeSelect>
                    </UiField>
                  </CardContent>
                </Card>
              ))}
              {rows && !rows.length && (
                <p className="px-1 text-sm text-muted-foreground">Sin seguimientos</p>
              )}
              <Button
                variant="ghost"
                size="sm"
                className="justify-start"
                aria-label={'Agregar seguimiento en ' + label}
                onClick={() => setCreate(id)}
              >
                <Plus />
                Agregar seguimiento
              </Button>
            </section>
          );
        })}
      </div>
      <FormDialog
        title="Nuevo seguimiento"
        open={!!create}
        onClose={() => setCreate(null)}
        fields={[
          { name: 'title', label: 'Título', required: true },
          { name: 'contactId', label: 'Contacto', type: 'contact', required: true },
          { name: 'value', label: 'Valor estimado (USD)', type: 'number', value: '0' },
          {
            name: 'stage',
            label: 'Etapa',
            required: true,
            value: create ?? 'new',
            options: stages.map(([value, label]) => ({ value, label })),
          },
        ]}
        onSubmit={async (v) => {
          await api('/opportunities', 'POST', { ...v, value: Number(v.value) });
          mutate();
        }}
      />
    </>
  );
}
