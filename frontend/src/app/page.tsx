import { getServerSession } from 'next-auth';
import { loginUrl, pathWithQuery } from '@/lib/login-return';
import { redirect } from 'next/navigation';
import { authOptions } from '@/lib/auth';
import Workspace from '@/components/workspace';
import PlatformWorkspace from '@/components/platform-workspace';
export default async function Page({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const session = await getServerSession(authOptions);
  if (!session) redirect(loginUrl(pathWithQuery('/', await searchParams)));
  // Workspace lee /me y /overview, negadas al dueño de plataforma: su vista es otra.
  if (session.platform)
    return (
      <PlatformWorkspace
        actingFor={session.platform.actingFor}
        name={session.user?.name ?? 'Dueño de plataforma'}
      />
    );
  return <Workspace />;
}
