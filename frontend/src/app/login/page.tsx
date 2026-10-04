export const dynamic = 'force-dynamic';
import { cookies, headers } from 'next/headers';
import { redirect } from 'next/navigation';
import type { NextRequest } from 'next/server';
import { getToken } from 'next-auth/jwt';
import { safeReturnPath } from '@/lib/login-return';
import Login from '@/components/login';
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export default async function Page({
  searchParams,
}: {
  searchParams: Promise<{ callbackUrl?: string; as?: string; step?: string }>;
}) {
  const params = await searchParams;
  const returnTo = safeReturnPath(params.callbackUrl);
  if (typeof params.as !== 'string' || !uuid.test(params.as)) return <Login returnTo={returnTo} />;
  // Hospital's «Abrir Recepción»: enter as that Keycloak subject (next-auth sets token.sub = profile.sub).
  const token = await getToken({
    req: {
      headers: Object.fromEntries(await headers()),
      cookies: Object.fromEntries((await cookies()).getAll().map((c) => [c.name, c.value])),
    } as unknown as NextRequest, // getToken only reads headers and cookies
    secret: process.env.AUTH_SECRET,
  });
  if (token?.sub?.toLowerCase() === params.as.toLowerCase()) redirect('/');
  const step = params.step === '1' ? 1 : params.step === '2' ? 2 : 0;
  return <Login returnTo={returnTo} hospital={{ as: params.as, step }} />;
}
