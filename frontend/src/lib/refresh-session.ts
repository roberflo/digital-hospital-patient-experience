import 'server-only';
import { createHash } from 'node:crypto';
type Tokens = { accessToken: string; refreshToken: string; accessExpires: number };
export class RefreshSessionError extends Error {
  status: number;
  constructor(status: number, message: string) {
    super(message);
    this.status = status;
  }
}
// Concurrent polling requests can share a rotating refresh token. Coalesce them in this process;
// retain successful results briefly so late requests with the old cookie get the same rotation.
const pending = new Map<string, { expires: number; result: Promise<Tokens> }>();
export async function refreshSession(refreshToken: string): Promise<Tokens> {
  const issuer = process.env.KEYCLOAK_ISSUER;
  const client = process.env.KEYCLOAK_CLIENT_ID;
  const secret = process.env.KEYCLOAK_CLIENT_SECRET;
  if (!issuer || !client || !secret)
    throw new RefreshSessionError(
      503,
      'No se pudo conectar con el acceso del hospital. Inténtalo de nuevo.',
    );
  const key = createHash('sha256')
    .update(issuer + '\0' + client + '\0' + refreshToken)
    .digest('hex');
  const now = Date.now();
  for (const [id, item] of pending) if (item.expires <= now) pending.delete(id);
  const cached = pending.get(key);
  if (cached) return cached.result;
  const result = (async (): Promise<Tokens> => {
    let response: Response;
    try {
      response = await fetch(`${issuer}/protocol/openid-connect/token`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: new URLSearchParams({
          grant_type: 'refresh_token',
          refresh_token: refreshToken,
          client_id: client,
          client_secret: secret,
        }),
        cache: 'no-store',
        signal: AbortSignal.timeout(8000),
        redirect: 'error',
      });
    } catch {
      throw new RefreshSessionError(
        503,
        'No pudimos renovar la sesión por un problema de conexión. Inténtalo de nuevo.',
      );
    }
    let value;
    try {
      value = await response.json();
    } catch {
      throw new RefreshSessionError(
        503,
        'El acceso del hospital no respondió correctamente. Inténtalo de nuevo.',
      );
    }
    if (!response.ok) {
      if (response.status === 400 && value?.error === 'invalid_grant')
        throw new RefreshSessionError(401, 'Tu sesión terminó. Inicia sesión para continuar.');
      // Invalid client credentials, rate limits and provider outages are not user session expiry.
      throw new RefreshSessionError(
        503,
        'El acceso del hospital no está disponible temporalmente. Inténtalo de nuevo.',
      );
    }
    if (
      typeof value?.access_token !== 'string' ||
      !value.access_token ||
      typeof value.expires_in !== 'number' ||
      !Number.isFinite(value.expires_in) ||
      value.expires_in <= 0
    )
      throw new RefreshSessionError(
        503,
        'No se pudo validar la renovación de la sesión. Inténtalo de nuevo.',
      );
    return {
      accessToken: value.access_token,
      refreshToken:
        typeof value.refresh_token === 'string' && value.refresh_token
          ? value.refresh_token
          : refreshToken,
      accessExpires: Date.now() + value.expires_in * 1000,
    };
  })();
  // Bound both memory and token retention. Deployment with multiple Node processes needs a shared
  // session/refresh coordinator; this does not change token or session lifetimes.
  if (pending.size >= 100) pending.delete(pending.keys().next().value!);
  const item = { expires: now + 12000, result };
  pending.set(key, item);
  try {
    const tokens = await result;
    item.expires = Date.now() + 5000;
    return tokens;
  } catch (error) {
    pending.delete(key);
    throw error;
  }
}
