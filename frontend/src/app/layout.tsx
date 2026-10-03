import type { Metadata } from 'next';
import { Toaster } from 'sonner';
import './globals.css';
import SessionRecovery from '@/components/session-recovery';
export const metadata: Metadata = {
  title: 'Recepción · CRM del hospital',
  description: 'Atención conectada para pacientes, equipos y agentes',
};
export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="es">
      <body>
        <SessionRecovery>{children}</SessionRecovery>
        <Toaster richColors position="bottom-right" />
      </body>
    </html>
  );
}
