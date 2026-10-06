import { signOut } from 'next-auth/react';
// Keep unsaved work only in this tab's memory. No patient drafts or tokens go to storage.
export const SESSION_EXPIRED = 'recepcion:session-expired';
export const SESSION_RESUMED = 'recepcion:session-resumed';
export const SESSION_IDENTITY = 'recepcion:session-identity';
export const SESSION_CHANNEL = 'recepcion-session';
let paused = false;
let generation = 0;
export class SessionExpiredError extends Error {
  readonly status = 401;
  constructor() {
    super('Tu sesión terminó. Inicia sesión para continuar.');
  }
}
export const isSessionPaused = () => paused;
export function expireSession() {
  if (paused) return;
  paused = true;
  window.dispatchEvent(new Event(SESSION_EXPIRED));
}
export function resumeSession() {
  generation++;
  paused = false;
  window.dispatchEvent(new Event(SESSION_RESUMED));
}
// «Cerrar sesión»: ends the Keycloak session too. Any failure still ends the local session and
// lands on /login.
export async function endSession() {
  let url = '/login';
  try {
    const end = await fetch('/api/session/end').then((r) => r.json());
    if (typeof end?.url === 'string') url = end.url;
  } catch {}
  try {
    await signOut({ redirect: false });
  } catch {}
  window.location.href = url;
}
// The platform owner has no /me (it is not a member of any hospital): session probes ask this.
// null = not a platform session.
export const platformSubject = (): Promise<string | null> =>
  fetch('/api/auth/session', { cache: 'no-store' })
    .then((r) => r.json())
    .then((s) => (typeof s?.platform?.sub === 'string' ? s.platform.sub : null))
    .catch(() => null);
// Dueño de plataforma: la recepción que pintó esta pestaña. Viaja en cada petición a /api/crm para
// que el proxy se niegue a actuar si la elección (una cookie de todo el navegador) ya es otra.
// Solo en memoria de la pestaña; una carga completa la olvida.
export const ACTING_LOST = 'recepcion:acting-lost';
export type ActingLost = 'acting_changed' | 'acting_expired';
let actingExpected: string | null = null;
export function expectActing(tenantId: string | null) {
  actingExpected = tenantId;
}
export async function sessionFetch(url: string, init?: RequestInit) {
  if (paused) throw new SessionExpiredError();
  const requestGeneration = generation;
  const scoped = !!actingExpected && url.startsWith('/api/crm/');
  if (scoped) {
    const headers = new Headers(init?.headers);
    headers.set('X-Acting-Expected', actingExpected!);
    init = { ...init, headers };
  }
  const response = await fetch(url, init);
  if (response.status === 401 && requestGeneration === generation) expireSession();
  if (scoped && response.status === 409) {
    const code = (
      await response
        .clone()
        .json()
        .catch(() => null)
    )?.code;
    if (code === 'acting_changed' || code === 'acting_expired')
      window.dispatchEvent(new CustomEvent<ActingLost>(ACTING_LOST, { detail: code }));
  }
  if (response.ok && url === '/api/crm/me') {
    const identity = await response.clone().json();
    window.dispatchEvent(new CustomEvent(SESSION_IDENTITY, { detail: identity }));
  }
  return response;
}
