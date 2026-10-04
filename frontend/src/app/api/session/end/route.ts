import { NextRequest, NextResponse } from 'next/server';
import { getToken } from 'next-auth/jwt';
export const dynamic = 'force-dynamic';
// Where the browser must go to end the Keycloak session. Clearing only Recepción's cookie would
// leave the identity provider signed in, and the next «Continuar» would silently re-enter as the
// same person — on a shared front desk, that is not a sign-out.
export async function GET(req: NextRequest) {
  const token = await getToken({ req, secret: process.env.AUTH_SECRET });
  const issuer = process.env.KEYCLOAK_ISSUER;
  if (!token?.idToken || !issuer) return NextResponse.json({ url: '/login' });
  const url = new URL(`${issuer}/protocol/openid-connect/logout`);
  url.searchParams.set('id_token_hint', token.idToken);
  url.searchParams.set('post_logout_redirect_uri', `${process.env.NEXTAUTH_URL}/login`);
  return NextResponse.json({ url: url.toString() });
}
