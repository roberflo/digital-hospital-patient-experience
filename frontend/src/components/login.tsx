'use client';
import { useEffect, useRef } from 'react';
import { signIn } from 'next-auth/react';
import { AlertCircle, ArrowRight, HeartPulse } from 'lucide-react';
import { Alert, AlertDescription } from './ui/alert';
import { Button } from './ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from './ui/card';
import { Field, FieldDescription, FieldGroup } from './ui/field';
// «Abrir Recepción» from Hospital: step 0 reuses any Keycloak session, step 1 forces credentials,
// step 2 means the wrong account came back twice, so stop instead of looping.
export default function Login({
  returnTo = '/',
  hospital,
}: {
  returnTo?: string;
  hospital?: { as: string; step: 0 | 1 | 2 };
}) {
  const auto = hospital && hospital.step < 2 ? hospital : undefined;
  const started = useRef(false);
  useEffect(() => {
    if (!auto || started.current) return;
    started.current = true;
    const callbackUrl = `/login?as=${auto.as}&step=${auto.step + 1}`;
    void signIn('keycloak', { callbackUrl }, auto.step ? { prompt: 'login' } : undefined);
  }, [auto]);
  return (
    <main className="flex min-h-svh flex-col items-center justify-center gap-6 bg-muted p-6 md:p-10">
      <div className="flex w-full max-w-sm flex-col gap-6">
        <div className="flex items-center gap-2 self-center font-medium">
          <div className="flex size-6 items-center justify-center rounded-md bg-primary text-primary-foreground">
            <HeartPulse className="size-4" />
          </div>
          Recepción
        </div>
        <Card>
          <CardHeader className="text-center">
            <CardTitle className="text-xl">
              <h1>
                {returnTo === '/session-restored' ? 'Recupera tu sesión' : 'Bienvenido a recepción'}
              </h1>
            </CardTitle>
            <CardDescription>Inicia sesión para continuar con la atención.</CardDescription>
          </CardHeader>
          <CardContent>
            <FieldGroup>
              {auto && (
                <p className="text-center text-sm text-muted-foreground" role="status">
                  Conectando con tu cuenta del hospital…
                </p>
              )}
              {hospital?.step === 2 && (
                <Alert variant="destructive">
                  <AlertCircle />
                  <AlertDescription>
                    Entraste con una cuenta distinta a la de Hospital. Cierra sesión o continúa con
                    la cuenta del hospital correcto.
                  </AlertDescription>
                </Alert>
              )}
              <Field>
                <Button onClick={() => signIn('keycloak', { callbackUrl: returnTo })}>
                  Continuar con mi cuenta del hospital
                  <ArrowRight />
                </Button>
                <FieldDescription className="text-center">
                  Si ya tienes una sesión abierta en Hospital, podrás continuar con ella.
                  Reconoceremos tu hospital y conservaremos tus permisos.
                </FieldDescription>
              </Field>
            </FieldGroup>
          </CardContent>
        </Card>
        <FieldDescription className="px-6 text-center">
          Tu acceso conserva los permisos del hospital.
        </FieldDescription>
      </div>
    </main>
  );
}
