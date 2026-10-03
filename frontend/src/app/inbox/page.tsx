import { getServerSession } from 'next-auth';
import { redirect } from 'next/navigation';
import { authOptions } from '@/lib/auth';
import KapsoInbox from '@/components/kapso-inbox';
export default async function InboxPage() {
  if (!(await getServerSession(authOptions))) redirect('/login');
  return <KapsoInbox />;
}
