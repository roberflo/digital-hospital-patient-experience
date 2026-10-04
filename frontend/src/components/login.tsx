'use client';
import { useEffect, useRef } from 'react';
import { signIn } from 'next-auth/react';
import { HeartPulse, ArrowRight, MessageCircle, CalendarCheck, ShieldCheck } from 'lucide-react';
import { Button } from './ui/button';
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
    <main className="login-shell">
      <section className="login-story">
        <div className="brand">
          <span className="brand-icon">
            <HeartPulse />
          </span>{' '}
          recepción<span className="brand-dot">.</span>
        </div>
        <div>
          <span className="eyebrow">CADA CONVERSACIÓN, MEJOR ATENCIÓN</span>
          <h1>
            Más cerca de
            <br />
            tus pacientes.
          </h1>
          <p>
            El equipo, la agenda y la información que necesitas.
            <br />
            Todo conectado en un solo lugar.
          </p>
          <div className="login-features">
            <span>
              <MessageCircle /> Atención por WhatsApp
            </span>
            <span>
              <CalendarCheck /> Agenda del hospital
            </span>
            <span>
              <ShieldCheck /> Acceso por empresa
            </span>
          </div>
        </div>
        <small>Un espacio para cuidar cada detalle.</small>
      </section>
      <section className="login-form">
        <div className="login-box">
          <span className="pill">TU ESPACIO DE TRABAJO</span>
          <h2>
            {returnTo === '/session-restored' ? 'Recupera tu sesión' : 'Bienvenido a recepción'}
          </h2>
          <p>Inicia sesión para continuar con la atención.</p>
          {auto && <p role="status">Conectando con tu cuenta del hospital…</p>}
          {hospital?.step === 2 && (
            <p role="alert">
              Entraste con una cuenta distinta a la de Hospital. Cierra sesión o continúa con la
              cuenta del hospital correcto.
            </p>
          )}
          <Button
            className="w-full mt-4"
            onClick={() => signIn('keycloak', { callbackUrl: returnTo })}
          >
            Continuar con mi cuenta del hospital
            <ArrowRight />
          </Button>
          <p className="hint">
            Si ya tienes una sesión abierta en Hospital, podrás continuar con ella. Reconoceremos tu
            hospital y conservaremos tus permisos.
          </p>
          <small>Tu acceso conserva los permisos del hospital.</small>
        </div>
      </section>
    </main>
  );
}
