'use client';
// docs/usage-report.md AC 15: lo que cada clínica usó en un mes, para el dueño de plataforma.
// Solo conteos: ningún nombre de paciente, teléfono ni mensaje viaja hasta aquí.
import { useState } from 'react';
import useSWR from 'swr';
import { AlertCircle, BarChart3, RefreshCw } from 'lucide-react';
import { fetcher } from '@/lib/api';
import { Alert, AlertDescription } from './ui/alert';
import { Button } from './ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from './ui/card';
import { Empty, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from './ui/empty';
import { Input } from './ui/input';
import { Label } from './ui/label';
import { Spinner } from './ui/spinner';
import {
  Table,
  TableBody,
  TableCell,
  TableFooter,
  TableHead,
  TableHeader,
  TableRow,
} from './ui/table';

type Counts = {
  numbersConnected: number;
  numbersEnabled: number;
  patientMessages: number;
  agentReplies: number;
  humanReplies: number;
  templatesSent: number;
  agentConversations: number;
  appointments: number;
  registrations: number;
  prescriptions: number;
  aiCalls: number | null;
  inputTokens: number | null;
  outputTokens: number | null;
  cachedInputTokens: number | null;
};
type Usage = {
  month: string;
  aiMetered: boolean;
  tenants: { id: string; name: string; usage: Counts }[];
  totals: Counts;
};

const columns: [keyof Counts, string][] = [
  ['numbersConnected', 'Números conectados'],
  ['numbersEnabled', 'Números habilitados'],
  ['patientMessages', 'Mensajes de pacientes'],
  ['agentReplies', 'Respuestas del agente'],
  ['humanReplies', 'Respuestas de personas'],
  ['templatesSent', 'Plantillas enviadas'],
  ['agentConversations', 'Conversaciones del agente'],
  ['appointments', 'Citas del agente'],
  ['registrations', 'Registros del agente'],
  ['prescriptions', 'Recetas del agente'],
  ['aiCalls', 'Llamadas a IA'],
  ['inputTokens', 'Tokens de entrada'],
  ['outputTokens', 'Tokens de salida'],
  ['cachedInputTokens', 'Tokens en caché'],
];
const number = new Intl.NumberFormat('es-SV');
const thisMonth = () => {
  const now = new Date();
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}`;
};
const MONTH = /^\d{4}-(0[1-9]|1[0-2])$/;

function Cells({ counts }: { counts: Counts }) {
  return columns.map(([key]) => (
    <TableCell key={key} className="text-right tabular-nums">
      {counts[key] == null ? (
        <span className="text-muted-foreground">sin medir</span>
      ) : (
        number.format(counts[key])
      )}
    </TableCell>
  ));
}

export function UsageReport() {
  const [month, setMonth] = useState(thisMonth);
  const valid = MONTH.test(month);
  const { data, error, mutate, isValidating } = useSWR<Usage>(
    valid ? `/platform/usage?month=${month}` : null,
    fetcher,
    { revalidateOnFocus: false },
  );
  // SWR keeps the last report when a revalidation fails: the failure wins, a stale month is not shown.
  const report = error || (data && !Array.isArray(data.tenants)) ? undefined : data;
  return (
    <Card role="region" aria-labelledby="usage-title" className="min-w-0">
      <CardHeader>
        <CardTitle>
          <h2 id="usage-title" className="flex items-center gap-2">
            <BarChart3 className="size-4" /> Uso del mes
          </h2>
        </CardTitle>
        <CardDescription>
          Conteos por clínica en su propia zona horaria. Los números de WhatsApp son los de hoy.
        </CardDescription>
      </CardHeader>
      <CardContent className="flex min-w-0 flex-col gap-4">
        <div className="flex flex-col gap-2 sm:max-w-48">
          <Label htmlFor="usage-month">Mes</Label>
          <Input
            id="usage-month"
            type="month"
            value={month}
            aria-invalid={!valid}
            onChange={(e) => setMonth(e.target.value)}
          />
        </div>
        {!valid ? (
          <p className="text-sm text-muted-foreground">Elige un mes (año y mes).</p>
        ) : error || (data && !report) ? (
          <Alert variant="destructive">
            <AlertCircle />
            <AlertDescription className="gap-3">
              <p>No pudimos leer el uso de este mes. La lectura falló: no significa cero.</p>
              <Button variant="outline" size="sm" disabled={isValidating} onClick={() => mutate()}>
                <RefreshCw /> Reintentar
              </Button>
            </AlertDescription>
          </Alert>
        ) : !report ? (
          <div className="flex items-center justify-center gap-2 p-6 text-sm text-muted-foreground">
            <Spinner /> Leyendo el uso…
          </div>
        ) : report.tenants.length === 0 ? (
          <Empty>
            <EmptyHeader>
              <EmptyMedia variant="icon">
                <BarChart3 />
              </EmptyMedia>
              <EmptyTitle>Aún no hay clínicas</EmptyTitle>
              <EmptyDescription>
                Cuando abras la recepción de un hospital, su uso aparecerá aquí.
              </EmptyDescription>
            </EmptyHeader>
          </Empty>
        ) : (
          <Table aria-label={`Uso por clínica, ${report.month}`}>
            <TableHeader>
              <TableRow>
                <TableHead className="sticky left-0 bg-card">Clínica</TableHead>
                {columns.map(([key, label]) => (
                  <TableHead key={key} className="text-right">
                    {label}
                  </TableHead>
                ))}
              </TableRow>
            </TableHeader>
            <TableBody>
              {report.tenants.map((t) => (
                <TableRow key={t.id}>
                  <TableHead scope="row" className="sticky left-0 max-w-56 truncate bg-card">
                    {t.name}
                  </TableHead>
                  <Cells counts={t.usage} />
                </TableRow>
              ))}
            </TableBody>
            <TableFooter>
              <TableRow>
                <TableHead scope="row" className="sticky left-0 bg-muted">
                  Total
                </TableHead>
                <Cells counts={report.totals} />
              </TableRow>
            </TableFooter>
          </Table>
        )}
      </CardContent>
    </Card>
  );
}
