'use client';
import { useEffect, useState } from 'react';
import useSWR from 'swr';
import { Building2 } from 'lucide-react';
import { fetcher } from '@/lib/api';
import { Badge } from './ui/badge';
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from './ui/card';
import { Empty, EmptyDescription, EmptyHeader, EmptyMedia } from './ui/empty';
import { Item, ItemContent, ItemDescription, ItemMedia, ItemTitle } from './ui/item';
import { Spinner } from './ui/spinner';
import { Tabs, TabsContent, TabsList, TabsTrigger } from './ui/tabs';
import { ErrorBox } from './workspace-shared';
import {
  HospitalCommercialLink,
  Pages,
  hint,
  money,
  type Company,
  type Page,
  type Service,
} from './commercial-shared';

export function HospitalCompanies({ search }: { search: string }) {
  const [page, setPage] = useState(1);
  const [tab, setTab] = useState('companies');
  useEffect(() => setPage(1), [search, tab]);
  const companies = useSWR<Page<Company>>(
    tab === 'companies'
      ? `/commercial/companies?q=${encodeURIComponent(search)}&page=${page}`
      : null,
    fetcher,
  );
  const services = useSWR<Page<Service>>(
    tab === 'services' ? `/commercial/services?q=${encodeURIComponent(search)}&page=${page}` : null,
    fetcher,
  );
  return (
    <Card>
      <CardHeader>
        <CardTitle>
          <h2>Empresas, convenios y servicios</h2>
        </CardTitle>
        <CardDescription>
          Datos del hospital. El convenio aplica un descuento porcentual general a sus servicios.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <Tabs value={tab} onValueChange={setTab} className="gap-4">
          <div className="flex min-w-0 flex-wrap items-center justify-between gap-2">
            <div className="max-w-full overflow-x-auto">
              <TabsList>
                <TabsTrigger value="companies">Empresas y convenios</TabsTrigger>
                <TabsTrigger value="services">Servicios y precios</TabsTrigger>
              </TabsList>
            </div>
            <HospitalCommercialLink />
          </div>
          <TabsContent value={tab} className="flex min-w-0 flex-col gap-4">
            <ErrorBox error={companies.error ?? services.error} />
            <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
              {tab === 'companies'
                ? companies.data?.items.map((c) => (
                    <Item variant="outline" asChild key={c.id}>
                      <article>
                        <ItemMedia variant="icon" className="self-start">
                          <Building2 />
                        </ItemMedia>
                        <ItemContent className="min-w-0 items-start">
                          <ItemTitle>
                            <h3>{c.name}</h3>
                          </ItemTitle>
                          <Badge variant={c.active ? 'secondary' : 'outline'}>
                            {c.active ? 'Empresa activa' : 'Inactiva'}
                          </Badge>
                          <ItemDescription>
                            {c.taxId || 'Sin identificación fiscal'}
                          </ItemDescription>
                          <strong className="text-sm font-semibold">
                            {c.active && c.agreement?.active
                              ? `${c.agreement.discountPercent}% de descuento`
                              : 'Sin descuento activo'}
                          </strong>
                          <p className="text-xs text-muted-foreground">
                            {c.agreement?.name ?? 'Sin convenio'}
                          </p>
                        </ItemContent>
                      </article>
                    </Item>
                  ))
                : services.data?.items.map((s) => (
                    <Item variant="outline" asChild key={s.id}>
                      <article>
                        <ItemContent className="min-w-0 items-start">
                          <Badge variant="outline">
                            {s.kind === 'consultation' ? 'Consulta' : 'Tratamiento'}
                          </Badge>
                          <ItemTitle>
                            <h3>{s.name}</h3>
                          </ItemTitle>
                          <ItemDescription>{s.code}</ItemDescription>
                          <strong className="text-sm font-semibold">
                            {money(s.price, s.currency)}
                          </strong>
                        </ItemContent>
                      </article>
                    </Item>
                  ))}
            </div>
            {!(tab === 'companies' ? companies.data : services.data) &&
              !companies.error &&
              !services.error && (
                <p className={`flex items-center gap-2 ${hint}`}>
                  <Spinner aria-hidden="true" role="presentation" />
                  Consultando Hospital…
                </p>
              )}
            {(tab === 'companies' ? companies.data?.total : services.data?.total) === 0 && (
              <Empty>
                <EmptyHeader>
                  <EmptyMedia variant="icon">
                    <Building2 />
                  </EmptyMedia>
                  <EmptyDescription>
                    No hay registros. Agrégalos desde Hospital para utilizarlos aquí.
                  </EmptyDescription>
                </EmptyHeader>
              </Empty>
            )}
          </TabsContent>
        </Tabs>
      </CardContent>
      <CardFooter>
        {tab === 'companies' ? (
          <Pages data={companies.data} page={page} setPage={setPage} />
        ) : (
          <Pages data={services.data} page={page} setPage={setPage} />
        )}
      </CardFooter>
    </Card>
  );
}
