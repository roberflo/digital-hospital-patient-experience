import type { NextAuthOptions } from 'next-auth';
import KeycloakProvider from 'next-auth/providers/keycloak';
import { cookies } from 'next/headers';
import { actingCookieName, platformSession, type ActingFor } from './acting.ts';
// Hospital's Keycloak realm is the only identity provider, in every environment.
const internalIssuer = process.env.KEYCLOAK_INTERNAL_ISSUER;
export const authOptions: NextAuthOptions = {
  secret: process.env.AUTH_SECRET,
  session: { strategy: 'jwt', maxAge: 3600 },
  pages: { signIn: '/login' },
  providers: [
    KeycloakProvider({
      clientId: process.env.KEYCLOAK_CLIENT_ID ?? '',
      clientSecret: process.env.KEYCLOAK_CLIENT_SECRET ?? '',
      issuer: process.env.KEYCLOAK_ISSUER,
      ...(internalIssuer
        ? {
            wellKnown: undefined,
            authorization: {
              url: `${process.env.KEYCLOAK_ISSUER}/protocol/openid-connect/auth`,
              params: { scope: 'openid email profile' },
            },
            token: `${internalIssuer}/protocol/openid-connect/token`,
            userinfo: `${internalIssuer}/protocol/openid-connect/userinfo`,
            jwks_endpoint: `${internalIssuer}/protocol/openid-connect/certs`,
          }
        : {}),
    }),
  ],
  callbacks: {
    async jwt({ token, account }) {
      if (account?.access_token) {
        token.accessToken = account.access_token;
        token.refreshToken = account.refresh_token;
        token.accessExpires = (account.expires_at ?? 0) * 1000;
        // Kept only as the hint that lets «Cerrar sesión» end the Keycloak session too.
        token.idToken = account.id_token;
      }
      return token;
    },
    // Dueño de plataforma: se deriva del access token en cada lectura (un refresco no lo deja
    // viejo) y de la cookie sellada de la recepción elegida. Sesión de hospital: sin cambios.
    async session({ session, token }) {
      const platform = await platformSession(
        token.accessToken,
        token.sub,
        (await cookies()).get(actingCookieName())?.value,
      );
      // `sub`: lets a paused tab tell «same owner, resume» from «someone else, clean page».
      if (platform) session.platform = { ...platform, sub: token.sub };
      return session;
    },
  },
};
declare module 'next-auth' {
  interface Session {
    platform?: { actingFor: ActingFor | null; sub?: string };
  }
}
declare module 'next-auth/jwt' {
  interface JWT {
    accessToken?: string;
    refreshToken?: string;
    accessExpires?: number;
    idToken?: string;
  }
}
