import { getServerSession } from 'next-auth';
import { loginUrl, pathWithQuery } from '@/lib/login-return';
import { redirect } from 'next/navigation';
import { authOptions } from '@/lib/auth';
import KapsoInbox from '@/components/kapso-inbox';
export default async function InboxPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const session = await getServerSession(authOptions);
  if (!session) redirect(loginUrl(pathWithQuery('/inbox', await searchParams)));
  // El dueño de plataforma solo configura: no abre la bandeja de ninguna recepción.
  if (session.platform) redirect('/');
  return <KapsoInbox />;
}
