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
  const session = await getServerSession(authOptions);
  if (!session) redirect(loginUrl(pathWithQuery('/whatsapp', await searchParams)));
  // Esta vista lee /me y enlaza a la bandeja; el dueño de plataforma conecta WhatsApp desde «/».
  if (session.platform) redirect('/');
  return <WhatsAppConnect />;
}
