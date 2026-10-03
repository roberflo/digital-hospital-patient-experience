import { getServerSession } from 'next-auth';
import { redirect } from 'next/navigation';
import { authOptions } from '@/lib/auth';
import WhatsAppConnect from '@/components/whatsapp-connect';
export default async function WhatsAppPage() {
  if (!(await getServerSession(authOptions))) redirect('/login');
  return <WhatsAppConnect />;
}
