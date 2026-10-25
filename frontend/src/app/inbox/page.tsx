import { redirect } from 'next/navigation';
// /inbox (Kapso directo) se retiró el 2026-10-25 (OQ-3): la única bandeja es la de la API.
// `phoneNumberId` no se reenvía: /?view=inbox solo lo usa junto con `phone`.
export default function InboxPage() {
  redirect('/?view=inbox');
}
