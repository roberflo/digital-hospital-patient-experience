import { getServerSession } from 'next-auth';
import { loginUrl, pathWithQuery } from '@/lib/login-return';
import { redirect } from 'next/navigation';
import { authOptions } from '@/lib/auth';
import WhatsAppConnect from '@/components/whatsapp-connect';
export default async function WhatsAppPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  if (!(await getServerSession(authOptions)))
    redirect(loginUrl(pathWithQuery('/whatsapp', await searchParams)));
  return <WhatsAppConnect />;
}
