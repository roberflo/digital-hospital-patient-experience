import type { NextAuthOptions } from 'next-auth';
import KeycloakProvider from 'next-auth/providers/keycloak';
import CredentialsProvider from 'next-auth/providers/credentials';
const demo =
  process.env.ALLOW_DEV_LOGIN === 'true' && process.env.ASPNETCORE_ENVIRONMENT === 'Development';
export const authOptions: NextAuthOptions = {
  secret: process.env.AUTH_SECRET,
  session: { strategy: 'jwt', maxAge: 3600 },
  pages: { signIn: '/login' },
  providers: demo
    ? [
        CredentialsProvider({
          name: 'Demostración',
          credentials: {
            user: { label: 'Usuario', type: 'text' },
            password: { label: 'Contraseña', type: 'password' },
          },
          async authorize(credentials) {
            const res = await fetch(`${process.env.API_URL}/auth/dev`, {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ user: credentials?.user, password: credentials?.password }),
              cache: 'no-store',
            });
            if (!res.ok) return null;
            const data = await res.json();
            return {
              id: credentials!.user!,
              name: credentials!.user!,
              accessToken: data.accessToken,
              accessExpires: Date.now() + data.expiresIn * 1000,
            };
          },
        }),
      ]
    : [
        KeycloakProvider({
          clientId: process.env.KEYCLOAK_CLIENT_ID ?? '',
          clientSecret: process.env.KEYCLOAK_CLIENT_SECRET ?? '',
          issuer: process.env.KEYCLOAK_ISSUER,
        }),
      ],
  callbacks: {
    async jwt({ token, account, user }) {
      if (account?.access_token) {
        token.accessToken = account.access_token;
        token.refreshToken = account.refresh_token;
        token.accessExpires = (account.expires_at ?? 0) * 1000;
      }
      if (user && 'accessToken' in user) {
        token.accessToken = user.accessToken;
        token.accessExpires = user.accessExpires;
      }
      return token;
    },
    async session({ session }) {
      return session;
    },
  },
};
declare module 'next-auth' {
  interface User {
    accessToken?: string;
    accessExpires?: number;
  }
}
declare module 'next-auth/jwt' {
  interface JWT {
    accessToken?: string;
    refreshToken?: string;
    accessExpires?: number;
  }
}
