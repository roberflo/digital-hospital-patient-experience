'use client';
import { signIn } from 'next-auth/react';
import { HeartPulse, ArrowRight, MessageCircle, CalendarCheck, ShieldCheck } from 'lucide-react';
import { Button } from './ui/button';
export default function Login({ returnTo = '/' }: { returnTo?: string }) {
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
