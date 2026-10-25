'use client';
import { useEffect, useState } from 'react';
import useSWR from 'swr';
import { Users, Plus } from 'lucide-react';
import { Badge } from './ui/badge';
import { Button } from './ui/button';
import { Card, CardAction, CardContent, CardFooter, CardHeader, CardTitle } from './ui/card';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from './ui/table';
import { api, fetcher, type Contact } from '@/lib/api';
import { whatsAppPhone } from '@/lib/format';
import { CustomerCommercial } from './customer-commercial';
import { PatientLink } from './hospital-connection';
import { PersonAvatar, EmptyState, ErrorBox, Loading } from './workspace-shared';
import { FormDialog } from './form-dialog';
export function ContactsView({ search }: { search: string }) {
  const [page, setPage] = useState(1);
  useEffect(() => setPage(1), [search]);
  const { data, error, mutate } = useSWR<Contact[]>(
    '/contacts?q=' + encodeURIComponent(search) + '&page=' + page,
    fetcher,
  );
  const [create, setCreate] = useState(false);
  const [edit, setEdit] = useState<Contact | null>(null);
  const [patientContact, setPatientContact] = useState<Contact | null>(null);
  return (
    <>
      <Card>
        <CardHeader>
          <CardTitle>
            <h2 className="flex items-center gap-2">
              {/* One page holds 100: a full page reads «100+», never a total it is not. */}
              Contactos{' '}
              <Badge variant="secondary">
                {data?.length === 100 ? '100+' : (data?.length ?? 0)}
              </Badge>
            </h2>
          </CardTitle>
          <CardAction>
            <Button size="sm" onClick={() => setCreate(true)}>
              <Plus />
              Nuevo contacto
            </Button>
          </CardAction>
        </CardHeader>
        <CardContent className="flex flex-col gap-4">
          <ErrorBox error={error} />
          {!data && !error ? (
            <Loading />
          ) : data?.length ? (
            <div className="w-0 min-w-full">
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Paciente / contacto</TableHead>
                    <TableHead className="hidden sm:table-cell">Teléfono</TableHead>
                    <TableHead className="hidden md:table-cell">Correo</TableHead>
                    <TableHead className="hidden md:table-cell">Etiqueta</TableHead>
                    <TableHead className="hidden md:table-cell">Relación / expediente</TableHead>
                    <TableHead />
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {data.map((c) => (
                    <TableRow key={c.id}>
                      <TableCell className="whitespace-normal">
                        <div className="flex items-center gap-2">
                          <PersonAvatar name={c.name} />
                          <div className="flex min-w-0 flex-col">
                            <strong className="font-medium break-words">{c.name}</strong>
                            <span className="text-xs text-muted-foreground sm:hidden">
                              {whatsAppPhone(c.phone)}
                            </span>
                          </div>
                        </div>
                      </TableCell>
                      <TableCell className="hidden sm:table-cell">
                        {whatsAppPhone(c.phone)}
                      </TableCell>
                      <TableCell className="hidden md:table-cell">{c.email || '—'}</TableCell>
                      <TableCell className="hidden md:table-cell">
                        {c.tags ? <Badge variant="secondary">{c.tags}</Badge> : '—'}
                      </TableCell>
                      <TableCell className="hidden md:table-cell">
                        <Badge variant={c.patientId ? 'secondary' : 'outline'}>
                          {c.isCustomer ? 'Cliente' : 'Contacto'} ·{' '}
                          {c.patientId ? 'Vinculado' : 'Por vincular'}
                        </Badge>
                      </TableCell>
                      <TableCell>
                        <div className="flex flex-col items-end gap-2 md:flex-row md:items-center md:justify-end">
                          <Button variant="outline" size="sm" onClick={() => setPatientContact(c)}>
                            {c.patientId ? 'Ver vínculo' : 'Vincular paciente'}
                          </Button>
                          <CustomerCommercial
                            contact={c}
                            onChange={() => {
                              mutate();
                            }}
                          />
                          <Button variant="ghost" size="sm" onClick={() => setEdit(c)}>
                            Editar
                          </Button>
                        </div>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
          ) : search.trim() ? (
            <EmptyState icon={Users} title={`Sin resultados para «${search.trim()}»`}>
              Prueba con otra parte del nombre o del teléfono.
            </EmptyState>
          ) : (
            <EmptyState icon={Users} title="Tu próxima relación empieza aquí">
              Agrega un contacto o recibe su primer mensaje por WhatsApp.
            </EmptyState>
          )}
        </CardContent>
        <CardFooter className="flex-wrap justify-between gap-2">
          <Button
            variant="outline"
            size="sm"
            disabled={page === 1}
            onClick={() => setPage(page - 1)}
          >
            Anterior
          </Button>
          <span className="text-sm text-muted-foreground">Página {page} · hasta 100 contactos</span>
          <Button
            variant="outline"
            size="sm"
            disabled={!data || data.length < 100}
            onClick={() => setPage(page + 1)}
          >
            Siguiente
          </Button>
        </CardFooter>
      </Card>
      {patientContact && (
        <PatientLink
          key={patientContact.id}
          contact={patientContact}
          open
          onClose={() => setPatientContact(null)}
          onChange={() => {
            mutate();
          }}
        />
      )}
      <FormDialog
        title={edit ? 'Editar contacto' : 'Nuevo contacto'}
        open={create || !!edit}
        onClose={() => {
          setCreate(false);
          setEdit(null);
        }}
        fields={[
          { name: 'name', label: 'Nombre completo', required: true, value: edit?.name },
          {
            name: 'phone',
            label: 'Teléfono',
            type: 'phone',
            required: true,
            value: edit?.phone,
            description:
              'Elige el país y escribe el número. Para otro país, elige «Otro» y escríbelo con + y su código.',
          },
          { name: 'email', label: 'Correo electrónico', type: 'email', value: edit?.email },
          { name: 'tags', label: 'Etiquetas', value: edit?.tags },
        ]}
        onSubmit={async (v) => {
          await api('/contacts' + (edit ? '/' + edit.id : ''), edit ? 'PUT' : 'POST', v);
          mutate();
        }}
      />
    </>
  );
}
/** Names for the cards, by id: the contact may sit past the first page of 100 (MR-17). */
