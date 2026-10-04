export const dynamic = 'force-dynamic';
import { safeReturnPath } from '@/lib/login-return';
import Login from '@/components/login';
export default async function Page({
  searchParams,
}: {
  searchParams: Promise<{ callbackUrl?: string }>;
}) {
  const params = await searchParams;
  return <Login returnTo={safeReturnPath(params.callbackUrl)} />;
}
