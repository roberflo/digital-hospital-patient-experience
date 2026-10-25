'use client';
import useSWR from 'swr';
import { ExternalLink } from 'lucide-react';
import { fetcher, type Me } from '@/lib/api';
import { Button } from './ui/button';

export type Page<T> = { items: T[]; total: number; page: number; pageSize: number };
export type Company = {
  id: string;
  name: string;
  active: boolean;
  taxId: string;
  agreement?: { name: string; active: boolean; discountPercent: number };
};
export type Service = {
  id: string;
  name: string;
  code: string;
  kind: string;
  price: number;
  currency: string;
};
export type Customer = { id: string; name: string; companyName?: string; source: string };
export type Quote = {
  quoteVersion: string;
  customerId?: string;
  companyId?: string;
  currency: string;
  subtotal: number;
  discountPercent: number;
  discountAmount: number;
  total: number;
  lines: { serviceId: string; name: string; quantity: number; unitPrice: number; total: number }[];
};
export type Purchase = { id: string; createdAt: string; status: string; quote: Quote };
export const money = (amount: number, currency = 'USD') =>
  new Intl.NumberFormat('es-SV', { style: 'currency', currency }).format(amount);
export const operator = (me?: Me) => !!me && ['admin', 'agent'].includes(me.role);
export const hint = 'text-sm text-muted-foreground';
export function HospitalCommercialLink() {
  const { data } = useSWR<{ hospitalUrl?: string }>('/commercial/settings', fetcher);
  return data?.hospitalUrl ? (
    <Button variant="link" size="sm" asChild>
      <a href={data.hospitalUrl} target="_blank" rel="noreferrer">
        Administrar en Hospital <ExternalLink />
      </a>
    </Button>
  ) : (
    <span className={hint}>Se administra en la sección Empresas y servicios de Hospital.</span>
  );
}
export function Pages<T>({
  data,
  page,
  setPage,
}: {
  data?: Page<T>;
  page: number;
  setPage: (page: number) => void;
}) {
  return (
    <div className="flex w-full flex-wrap items-center justify-between gap-2">
      <Button size="sm" variant="outline" disabled={page === 1} onClick={() => setPage(page - 1)}>
        Anterior
      </Button>
      <span className={hint}>
        Página {page} · {data?.total ?? 0} registros
      </span>
      <Button
        size="sm"
        variant="outline"
        disabled={!data || page * data.pageSize >= data.total}
        onClick={() => setPage(page + 1)}
      >
        Siguiente
      </Button>
    </div>
  );
}
