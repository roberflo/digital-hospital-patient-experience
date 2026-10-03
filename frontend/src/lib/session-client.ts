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
export async function sessionFetch(url: string, init?: RequestInit) {
  if (paused) throw new SessionExpiredError();
  const requestGeneration = generation;
  const response = await fetch(url, init);
  if (response.status === 401 && requestGeneration === generation) expireSession();
  if (response.ok && url === '/api/crm/me') {
    const identity = await response.clone().json();
    window.dispatchEvent(new CustomEvent(SESSION_IDENTITY, { detail: identity }));
  }
  return response;
}
