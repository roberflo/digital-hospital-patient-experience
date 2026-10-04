import type { NextAuthOptions } from 'next-auth';
import KeycloakProvider from 'next-auth/providers/keycloak';
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
    async session({ session }) {
      return session;
    },
  },
};
declare module 'next-auth/jwt' {
  interface JWT {
    accessToken?: string;
    refreshToken?: string;
    accessExpires?: number;
    idToken?: string;
  }
}
