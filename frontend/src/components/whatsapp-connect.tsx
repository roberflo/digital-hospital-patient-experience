'use client';
import Link from 'next/link';
import useSWR from 'swr';
import { ArrowLeft, Info, MessageCircle, ShieldCheck, TriangleAlert } from 'lucide-react';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Empty, EmptyDescription, EmptyHeader, EmptyMedia } from '@/components/ui/empty';
import {
  Item,
  ItemActions,
  ItemContent,
  ItemDescription,
  ItemMedia,
  ItemTitle,
} from '@/components/ui/item';
import { Spinner } from '@/components/ui/spinner';
import { useWhatsAppConnect, WhatsAppLinkFlow, whatsappApi as api } from './whatsapp-link-flow';
type Channel = {
  id: string;
  name: string;
  phoneNumberId: string;
  coexistence: boolean;
  doctorId?: string;
};
export default function WhatsAppConnect() {
  const { data: me, error: authError } = useSWR<{ role: string; name: string }>('/me', api);
  const { data: channels, mutate } = useSWR<Channel[]>('/channels', api);
  const admin = !!me && me.role === 'admin';
  const flow = useWhatsAppConnect(admin, mutate);
  return (
    <main className="min-h-dvh bg-background">
      <div className="mx-auto flex w-full max-w-4xl flex-col gap-6 p-4 md:p-10">
        <Button variant="ghost" className="self-start" asChild>
          <Link href="/?view=inbox">
            <ArrowLeft /> Volver a Bandeja de entrada
          </Link>
        </Button>
        <header className="flex items-start gap-4">
          <ItemMedia variant="icon">
            <MessageCircle />
          </ItemMedia>
          <div className="flex min-w-0 flex-col gap-2">
            <h1 className="text-2xl font-semibold tracking-tight">
              Conecta el WhatsApp de tu hospital
            </h1>
            <p className="text-sm text-muted-foreground">
              Administra los números conectados. La atención del equipo y el seguimiento se
              gestionan en Bandeja de entrada.
            </p>
          </div>
        </header>
        {authError && (
          <Alert variant="destructive">
            <TriangleAlert />
            <AlertDescription>
              {authError.code === 'hospital_not_onboarded' ? (
                authError.message
              ) : (
                <p>
                  No se pudo validar tu sesión.{' '}
                  <Link href="/login" className="underline underline-offset-4">
                    Inicia sesión
                  </Link>
                  .
                </p>
              )}
            </AlertDescription>
          </Alert>
        )}
        {!me && !authError && (
          <p className="flex items-center gap-2 text-sm text-muted-foreground">
            <Spinner /> Comprobando acceso…
          </p>
        )}
        {me && !admin && (
          <Card>
            <CardHeader>
              <CardTitle>
                <h2>Conexión administrada por tu hospital</h2>
              </CardTitle>
              <CardDescription>
                Un administrador del negocio debe conectar los números de recepción y de los
                doctores.
              </CardDescription>
            </CardHeader>
          </Card>
        )}
        {admin && (
          <Card>
            <CardContent className="flex flex-col gap-4">
              <ol className="flex flex-col gap-4 text-sm">
                <li className="flex flex-col gap-1">
                  <strong className="font-medium">1. Inicia la conexión segura</strong>
                  <p className="text-muted-foreground">
                    Genera el enlace de tu hospital. No necesitas claves API ni identificadores
                    técnicos.
                  </p>
                </li>
                <li className="flex flex-col gap-1">
                  <strong className="font-medium">2. Vincula tu número en Kapso</strong>
                  <p className="text-muted-foreground">
                    Inicia sesión con la cuenta de Meta que administra tu negocio y completa la
                    verificación. Elige Coexistence si quieres seguir usando WhatsApp Business en tu
                    teléfono.
                  </p>
                </li>
                <li className="flex flex-col gap-1">
                  <strong className="font-medium">3. Vuelve a Recepción</strong>
                  <p className="text-muted-foreground">
                    Verificaremos el número y lo agregaremos a este hospital. Puedes conectar varios
                    números repitiendo el proceso.
                  </p>
                </li>
              </ol>
              <Alert role="note">
                <Info />
                <AlertDescription>
                  Se conecta un número que ya tienes. Las tarifas de mensajes de Meta se administran
                  en la cuenta del negocio.
                </AlertDescription>
              </Alert>
              <WhatsAppLinkFlow flow={flow} />
            </CardContent>
          </Card>
        )}
        <Card>
          <CardHeader>
            <CardTitle>
              <h2>Números de este hospital</h2>
            </CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col gap-2">
            {channels
              ?.filter((c) => !c.phoneNumberId.startsWith('demo'))
              .map((c) => (
                <Item key={c.id} variant="muted">
                  <ItemContent className="min-w-48">
                    <ItemTitle>{c.name}</ItemTitle>
                    <ItemDescription>
                      {c.coexistence ? 'Coexistence · teléfono y Recepción' : 'Conexión dedicada'} ·{' '}
                      {c.doctorId ? 'Doctor' : 'Atención general'}
                    </ItemDescription>
                  </ItemContent>
                  <ItemActions>
                    <Button variant="outline" size="sm" asChild>
                      <Link href={`/inbox?phoneNumberId=${encodeURIComponent(c.phoneNumberId)}`}>
                        Consultar historial →
                      </Link>
                    </Button>
                  </ItemActions>
                </Item>
              ))}
            {channels && !channels.some((c) => !c.phoneNumberId.startsWith('demo')) && (
              <Empty>
                <EmptyHeader>
                  <EmptyMedia variant="icon">
                    <MessageCircle />
                  </EmptyMedia>
                  <EmptyDescription>Aún no tienes números conectados.</EmptyDescription>
                </EmptyHeader>
              </Empty>
            )}
          </CardContent>
        </Card>
        <p className="flex items-start gap-2 text-xs text-muted-foreground">
          <ShieldCheck className="size-4 shrink-0" />
          <span>
            El número se verifica con Kapso y queda asociado únicamente a tu hospital. El alta no
            activa respuestas automáticas.
          </span>
        </p>
      </div>
    </main>
  );
}
