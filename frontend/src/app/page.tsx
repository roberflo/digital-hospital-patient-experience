import { getServerSession } from 'next-auth';
import { loginUrl, pathWithQuery } from '@/lib/login-return';
import { redirect } from 'next/navigation';
import { authOptions } from '@/lib/auth';
import Workspace from '@/components/workspace';
export default async function Page({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const session = await getServerSession(authOptions);
  if (!session) redirect(loginUrl(pathWithQuery('/', await searchParams)));
  return <Workspace />;
}
