import 'server-only';
import { encode, decode } from 'next-auth/jwt';
// Dueño de plataforma (docs/platform-owner.md): la recepción en cuyo nombre actúa vive en una
// cookie sellada del servidor, ligada a su `sub`. Nunca en localStorage, la URL ni una cabecera
// del navegador; solo `crm-proxy.ts` la convierte en `X-Acting-Tenant`.
export type ActingFor = { tenantId: string; name: string };
const SALT = 'recepcion.acting';
// Doce horas, como en Hospital. La sesión sigue renovándose aparte; esto solo recuerda la elección.
const ACTING_MAX_AGE = 12 * 3600;
export const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const secure = () => !!process.env.NEXTAUTH_URL?.startsWith('https:');
export const actingCookieName = () => (secure() ? '__Secure-' : '') + SALT;
export const actingCookieOptions = (maxAge = ACTING_MAX_AGE) => ({
  httpOnly: true,
  secure: secure(),
  sameSite: 'lax' as const,
  path: '/',
  maxAge,
});
// Pista para la interfaz y para decidir si se adjunta la cabecera: la API vuelve a validar el
// token firmado y es la que manda.
export function isPlatformOwner(accessToken?: string) {
  try {
    const roles = JSON.parse(Buffer.from(accessToken!.split('.')[1], 'base64url').toString())
      ?.realm_access?.roles;
    return Array.isArray(roles) && roles.includes('platform-owner');
  } catch {
    return false;
  }
}
export const sealActing = (sub: string, { tenantId, name }: ActingFor) =>
  encode({
    token: { sub, tenantId, name },
    secret: process.env.AUTH_SECRET!,
    salt: SALT,
    maxAge: ACTING_MAX_AGE,
  });
// null = sin elección: no hay cookie, caducó, no descifra, o se selló para otra persona.
export async function readActing(value?: string, sub?: string): Promise<ActingFor | null> {
  if (!value || !sub) return null;
  try {
    const sealed = await decode({ token: value, secret: process.env.AUTH_SECRET!, salt: SALT });
    if (sealed?.sub !== sub || typeof sealed.tenantId !== 'string' || !UUID.test(sealed.tenantId))
      return null;
    return { tenantId: sealed.tenantId, name: typeof sealed.name === 'string' ? sealed.name : '' };
  } catch {
    return null;
  }
}
// `session.platform`: ausente = sesión de hospital.
export async function platformSession(accessToken?: string, sub?: string, cookie?: string) {
  return isPlatformOwner(accessToken) ? { actingFor: await readActing(cookie, sub) } : undefined;
}
