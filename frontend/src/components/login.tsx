'use client';
import { signIn } from 'next-auth/react';
import { useState } from 'react';
import { HeartPulse, ArrowRight, MessageCircle, CalendarCheck, ShieldCheck } from 'lucide-react';
import { Button } from './ui/button';
export default function Login({
  demo,
  hospitalLogin = false,
  hospitalDemo,
  returnTo = '/',
}: {
  demo: boolean;
  hospitalLogin?: boolean;
  hospitalDemo: boolean;
  returnTo?: string;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
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
          {demo ? (
            <form
              onSubmit={async (e) => {
                e.preventDefault();
                setBusy(true);
                setError('');
                const f = new FormData(e.currentTarget);
                const r = await signIn('credentials', {
                  user: f.get('user'),
                  password: f.get('password'),
                  redirect: false,
                });
                if (r?.ok) window.location.href = returnTo;
                else {
                  setError('No fue posible iniciar sesión. Comprueba tus datos.');
                  setBusy(false);
                }
              }}
            >
              <label>
                Usuario de demostración
                <select name="user">
                  <option value="admin">Administrador</option>
                  <option value="agent">Recepcionista</option>
                  <option value="doctor">Doctor</option>
                  {hospitalDemo && (
                    <option value="hospital">Hospital local · citas de prueba</option>
                  )}
                </select>
              </label>
              <label>
                Contraseña
                <input name="password" type="password" required autoComplete="current-password" />
              </label>
              <p className="hint">
                Entorno de prueba con datos sintéticos. Contraseña local: demo-recepcion.
              </p>
              {error && (
                <p role="alert" className="error">
                  {error}
                </p>
              )}
              <Button className="w-full" disabled={busy}>
                {busy ? 'Conectando…' : 'Entrar al espacio'}
                <ArrowRight />
              </Button>
            </form>
          ) : (
            <Button
              className="w-full"
              onClick={() => signIn('keycloak', { callbackUrl: returnTo })}
            >
              Continuar con mi cuenta del hospital
              <ArrowRight />
            </Button>
          )}
          {demo && hospitalLogin && (
            <Button
              className="w-full mt-4"
              variant="outline"
              onClick={() => signIn('keycloak', { callbackUrl: returnTo })}
            >
              Continuar con mi cuenta del hospital
              <ArrowRight />
            </Button>
          )}
          <small>Tu acceso conserva los permisos del hospital.</small>
        </div>
      </section>
    </main>
  );
}
