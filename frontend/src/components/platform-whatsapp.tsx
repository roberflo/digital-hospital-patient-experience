'use client';
import { useState } from 'react';
import useSWR from 'swr';
import {
  AlertCircle,
  Copy,
  MessageCircle,
  Pause,
  Play,
  RefreshCw,
  ShieldCheck,
  Wrench,
} from 'lucide-react';
import { toast } from 'sonner';
import { api, fetcher, type Channel } from '@/lib/api';
import { whatsappStep, type Installation } from '@/lib/first-steps';
import { Alert, AlertDescription, AlertTitle } from './ui/alert';
import { Button } from './ui/button';
import { Card, CardContent, CardHeader, CardTitle } from './ui/card';
import { Separator } from './ui/separator';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from './ui/table';
import { ChannelConnection } from './conversation-workspace';
import { useWhatsAppConnect, WhatsAppLinkFlow } from './whatsapp-link-flow';

export function WhatsAppSection() {
  const installation = useSWR<Installation>('/platform/installation', fetcher);
  const channels = useSWR<Channel[]>('/channels', fetcher);
  // Same key as «Conexión con Hospital»: one read.
  const connection = useSWR<{ hospital?: { agentEnabled?: boolean } }>(
    '/hospital/connection',
    fetcher,
  );
  const flow = useWhatsAppConnect(true, () => channels.mutate());
  // With the agent on, no number may be enabled from here: «Habilitar» is not offered at all.
  // Reminders only block the one number they go out through, which the API does not name, so
  // that is learned per number from its 409.
  const [agentRefused, setAgentRefused] = useState(false);
  const [reminderNumbers, setReminderNumbers] = useState<string[]>([]);
  const agentActive = agentRefused || connection.data?.hospital?.agentEnabled === true;
  // Reuses the administrator guide's rule for what the installation lacks, by setting name.
  const install = whatsappStep(installation, { data: [] }, {});
  const rows = channels.error
    ? undefined
    : channels.data?.filter((c) => !c.phoneNumberId.startsWith('demo'));
  async function toggle(channel: Channel) {
    try {
      await api('/channels/' + channel.id, 'PATCH', { enabled: !channel.enabled });
      await channels.mutate();
      toast.success(channel.enabled ? 'Número pausado' : 'Número habilitado');
    } catch (e) {
      const { status, code } = e as { status?: number; code?: string };
      // The page-level notice already says the reception changed or the choice expired.
      if (code === 'acting_changed' || code === 'acting_expired') return;
      if (!channel.enabled && status === 409 && code === 'reminders_enabled')
        setReminderNumbers((ids) => [...ids, channel.id]);
      else if (!channel.enabled && status === 409) setAgentRefused(true);
      else toast.error((e as Error).message);
    }
  }
  return (
    <Card role="region" aria-labelledby="platform-whatsapp-title">
      <CardHeader>
        <CardTitle>
          <h2 id="platform-whatsapp-title" className="flex items-center gap-2">
            <MessageCircle className="size-4" /> WhatsApp
          </h2>
        </CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {install.state === 'loading' && (
          <p className="text-sm text-muted-foreground">Comprobando la instalación…</p>
        )}
        {install.state === 'unknown' && (
          <Alert variant="destructive">
            <AlertCircle />
            <AlertDescription className="gap-3">
              <p>No pudimos leer el estado de la instalación. No sabemos si se puede conectar.</p>
              <Button variant="outline" size="sm" onClick={() => installation.mutate()}>
                <RefreshCw /> Reintentar
              </Button>
            </AlertDescription>
          </Alert>
        )}
        {install.state === 'blocked' && (
          <Alert role="status">
            <Wrench aria-hidden="true" />
            <AlertTitle className="line-clamp-none">
              Tarea del operador de la instalación.
            </AlertTitle>
            <AlertDescription>
              <p>
                Falta configurar{' '}
                {install.missing?.map((name, n) => (
                  <span key={name}>
                    {n > 0 && ', '}
                    <code className="break-all">{name}</code>
                  </span>
                ))}
                . Hasta entonces no se puede conectar ni verificar un número desde aquí.
              </p>
            </AlertDescription>
          </Alert>
        )}
        {install.state === 'pending' && (
          <>
            <p className="max-w-prose text-sm text-muted-foreground">
              Genera el enlace y envíalo a quien administra el número en Meta, en el hospital: es
              esa persona quien lo abre. Cuando termine, pulsa Verificar conexión. Conectar un
              número no activa respuestas automáticas.
            </p>
            <WhatsAppLinkFlow flow={flow} />
            {flow.link && (
              <Button
                variant="outline"
                size="sm"
                className="self-start"
                onClick={async () => {
                  try {
                    await navigator.clipboard.writeText(flow.link!.url);
                    toast.success('Enlace copiado');
                  } catch {
                    toast.error('No se pudo copiar. Copia el enlace desde el botón de Kapso.');
                  }
                }}
              >
                <Copy /> Copiar enlace para el hospital
              </Button>
            )}
          </>
        )}
        {agentActive && (
          <Alert role="status">
            <ShieldCheck aria-hidden="true" />
            <AlertDescription>
              El agente de esta recepción está activo; habilitar el número lo pondría a contestar.
              Lo habilita el Administrador del hospital. Desde aquí puedes pausarlo.
            </AlertDescription>
          </Alert>
        )}
        {reminderNumbers.length > 0 && (
          <Alert role="status">
            <ShieldCheck aria-hidden="true" />
            <AlertDescription>
              Los recordatorios automáticos de esta recepción están activos; habilitar el número
              volvería a enviarlos. Lo habilita el Administrador del hospital.
            </AlertDescription>
          </Alert>
        )}
        <Separator />
        <h3 className="text-sm font-medium">Números de esta recepción</h3>
        {channels.error ? (
          <Alert variant="destructive">
            <AlertCircle />
            <AlertDescription className="gap-3">
              <p>No pudimos leer los números de esta recepción.</p>
              <Button variant="outline" size="sm" onClick={() => channels.mutate()}>
                <RefreshCw /> Reintentar
              </Button>
            </AlertDescription>
          </Alert>
        ) : !rows ? (
          <p className="text-sm text-muted-foreground">Leyendo los números…</p>
        ) : rows.length === 0 ? (
          <p className="text-sm text-muted-foreground">
            Esta recepción aún no tiene números conectados.
          </p>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Canal</TableHead>
                <TableHead>Atención</TableHead>
                <TableHead>Uso en el celular</TableHead>
                <TableHead>Conexión</TableHead>
                <TableHead>Estado</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {rows.map((c) => (
                <TableRow key={c.id}>
                  <TableCell>
                    <div className="flex items-center gap-2">
                      <MessageCircle className="size-4 text-muted-foreground" />
                      <span className="font-medium">{c.name}</span>
                    </div>
                  </TableCell>
                  <TableCell>{c.doctorId ? 'Doctor' : 'General'}</TableCell>
                  <TableCell>
                    {c.coexistence ? 'WhatsApp Business y Recepción' : 'Recepción'}
                  </TableCell>
                  <TableCell>
                    {install.state === 'pending' ? (
                      <ChannelConnection id={c.id} />
                    ) : (
                      <span className="text-muted-foreground">
                        {install.state === 'blocked' ? 'Pendiente del operador' : '—'}
                      </span>
                    )}
                  </TableCell>
                  <TableCell>
                    {c.enabled ? (
                      <Button variant="outline" size="sm" onClick={() => toggle(c)}>
                        <Pause /> Pausar
                      </Button>
                    ) : agentActive || reminderNumbers.includes(c.id) ? (
                      <span className="text-muted-foreground">
                        Pausado · lo habilita el Administrador
                      </span>
                    ) : (
                      <Button variant="outline" size="sm" onClick={() => toggle(c)}>
                        <Play /> Habilitar
                      </Button>
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </CardContent>
    </Card>
  );
}
