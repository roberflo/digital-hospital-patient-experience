import { getServerSession } from 'next-auth';
import { redirect } from 'next/navigation';
import { authOptions } from '@/lib/auth';
import { loginUrl } from '@/lib/login-return';
import SessionRestored from '@/components/session-restored';
export default async function Page() {
  if (!(await getServerSession(authOptions))) redirect(loginUrl('/session-restored'));
  return <SessionRestored />;
}
