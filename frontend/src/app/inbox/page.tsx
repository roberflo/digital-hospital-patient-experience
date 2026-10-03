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
  if (!(await getServerSession(authOptions)))
    redirect(loginUrl(pathWithQuery('/inbox', await searchParams)));
  return <KapsoInbox />;
}
