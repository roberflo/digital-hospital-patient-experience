'use client';
import { useState } from 'react';
import useSWR from 'swr';
import {
  Users,
  Activity as ActivityIcon,
  Settings,
  ArrowUpRight,
  Send,
  Sparkles,
  ShieldCheck,
} from 'lucide-react';
import { toast } from 'sonner';
import { Alert, AlertDescription } from './ui/alert';
import { Badge } from './ui/badge';
import { Button } from './ui/button';
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from './ui/card';
import { Field as UiField, FieldDescription, FieldLabel } from './ui/field';
import { Item, ItemActions, ItemContent, ItemTitle } from './ui/item';
import { Spinner } from './ui/spinner';
import { Textarea } from './ui/textarea';
import { api, fetcher, type Me, type Activity } from '@/lib/api';
import { AppointmentReminders, TomorrowReminders } from './appointment-reminders';
import { StatusBadge, EmptyState, ErrorBox, ActivityTimeline } from './workspace-shared';
import { ContactPicker } from './form-dialog';
export function AgentView({ me }: { me: Me }) {
  const { data, error } = useSWR<Activity[]>('/activities', fetcher, { refreshInterval: 5000 });
  const admin = me.role === 'admin';
  const agentActions = data?.filter((a) => a.actorRole === 'agent_ai');
  const [contactId, setContactId] = useState('');
  const [query, setQuery] = useState('');
  const [answer, setAnswer] = useState('');
  const [busy, setBusy] = useState(false);
  return (
    <>
      <Card>
        <CardHeader>
          <CardDescription>TU EQUIPO, CON MÁS CAPACIDAD</CardDescription>
          <CardTitle>
            <h2 className="flex items-center gap-2">
              <Sparkles className="size-4" /> Agente de atención
            </h2>
          </CardTitle>
          <CardDescription>
            Responde preguntas, acompaña la agenda y entrega la atención a la persona correcta.
          </CardDescription>
          <CardAction>
            <StatusBadge value={me.tenant.agentEnabled ? 'agent' : 'human'} />
          </CardAction>
        </CardHeader>
        <CardContent className="flex flex-wrap gap-2">
          <Badge variant="outline">
            <ShieldCheck /> Contexto de tu hospital
          </Badge>
          <Badge variant="outline">
            <Users /> Transferencia a humanos
          </Badge>
          <Badge variant="outline">
            <ActivityIcon /> Historial de cada acción
          </Badge>
        </CardContent>
      </Card>
      <div className="grid items-start gap-6 lg:grid-cols-5">
        <Card className="min-w-0 lg:col-span-3">
          <CardHeader>
            <CardTitle>
              <h2 className="flex items-center gap-2">
                <Sparkles className="size-4" /> Asistente del equipo
              </h2>
            </CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col gap-4">
            <div className="flex flex-col gap-1">
              <h3 className="font-medium">¿En qué trabajamos hoy?</h3>
              <p className="text-sm text-muted-foreground">
                Consulta las métricas o selecciona un contacto para guardar una nota o crear un
                seguimiento.
              </p>
            </div>
            <UiField>
              <FieldLabel htmlFor="assistant-contact">Contacto para acciones</FieldLabel>
              <ContactPicker
                id="assistant-contact"
                label="Contacto para acciones"
                empty="Solo consultar métricas"
                onChange={setContactId}
              />
              <FieldDescription>
                La ficha del contacto seleccionado queda en Recepción.
              </FieldDescription>
            </UiField>
            <div className="flex flex-col gap-2">
              {[
                'Resume las conversaciones pendientes',
                '¿Qué seguimientos están pendientes?',
                'Crea un seguimiento para el contacto seleccionado',
              ].map((s) => (
                <Item
                  asChild
                  variant="outline"
                  size="sm"
                  className="text-left hover:bg-accent/50"
                  key={s}
                >
                  <button onClick={() => setQuery(s)}>
                    <ItemContent>
                      <ItemTitle>{s}</ItemTitle>
                    </ItemContent>
                    <ItemActions>
                      <ArrowUpRight className="size-4" />
                    </ItemActions>
                  </button>
                </Item>
              ))}
            </div>
            {answer && (
              <Alert>
                <Sparkles />
                <AlertDescription className="break-words whitespace-pre-wrap">
                  {answer}
                </AlertDescription>
              </Alert>
            )}
            <form
              className="flex flex-col items-start gap-3"
              onSubmit={async (e) => {
                e.preventDefault();
                setBusy(true);
                try {
                  const r = await api<{ answer: string }>('/assistant', 'POST', {
                    message: query,
                    contactId: contactId || null,
                  });
                  setAnswer(r.answer);
                  setQuery('');
                } catch (e) {
                  toast.error((e as Error).message);
                } finally {
                  setBusy(false);
                }
              }}
            >
              <Textarea
                aria-label="Pregunta al asistente"
                rows={3}
                placeholder="Pregunta sobre la atención de tu hospital…"
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                maxLength={2000}
              />
              <Button disabled={busy || !query.trim()}>
                {busy ? <Spinner /> : <Send />}
                {busy ? 'Consultando…' : 'Consultar al agente'}
              </Button>
            </form>
          </CardContent>
        </Card>
        <Card className="min-w-0 lg:col-span-2">
          <CardHeader>
            <CardTitle>
              <h2>Acciones recientes</h2>
            </CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col gap-4">
            <ErrorBox error={error} />
            <ActivityTimeline
              items={agentActions?.slice(0, 10)}
              proposal="Propuesta de cita registrada"
              zone={me.tenant.timeZone}
            />
            {data && !agentActions?.length && (
              <EmptyState icon={Sparkles} title="Listo para acompañar">
                Las acciones aparecerán al habilitar la atención automática.
              </EmptyState>
            )}
          </CardContent>
        </Card>
      </div>
      {(admin || me.role === 'agent') && <TomorrowReminders me={me} />}
      {admin && <AppointmentReminders me={me} />}
      {admin && (
        <Alert>
          <Settings />
          <AlertDescription>
            Personaliza las instrucciones y habilita el agente desde Configuración.
          </AlertDescription>
        </Alert>
      )}
    </>
  );
}
