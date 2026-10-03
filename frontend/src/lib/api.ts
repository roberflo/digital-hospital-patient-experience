import { sessionFetch } from './session-client';
export async function api<T = unknown>(
  path: string,
  method = 'GET',
  body?: unknown,
  requestKey?: string,
): Promise<T> {
  const res = await sessionFetch('/api/crm' + path, {
    method,
    headers:
      body instanceof FormData
        ? { 'Idempotency-Key': requestKey ?? crypto.randomUUID() }
        : {
            'Content-Type': 'application/json',
            'Idempotency-Key': requestKey ?? crypto.randomUUID(),
          },
    body: body instanceof FormData ? body : body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) {
    let message = 'No se pudo completar la operación';
    try {
      const p = await res.json();
      message = p.title ?? message;
    } catch {}
    if (res.status === 401) message = 'Tu sesión expiró. Vuelve a iniciar sesión.';
    throw Object.assign(new Error(message), { status: res.status });
  }
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}
export const fetcher = <T>(path: string) => api<T>(path);
export type Contact = {
  isCustomer?: boolean;
  hospitalCustomerId?: string;
  hospitalCompanyId?: string;
  hospitalCompanyName?: string;
  customerSince?: string;
  customerSource?: string;
  id: string;
  name: string;
  phone: string;
  email: string;
  tags: string;
  patientId?: string;
  companyId?: string;
  lifecycleStage: string;
};
export type Conversation = {
  state: string;
  priority: string;
  labels: string;
  revision: number;
  snoozedUntil?: string;
  lastMessage?: string;
  id: string;
  contactId: string;
  status: string;
  assignedTo?: string;
  summary: string;
  updatedAt: string;
  lastInboundAt?: string;
  channelId: string;
};
export type Channel = {
  id: string;
  name: string;
  phoneNumberId: string;
  doctorId?: string;
  coexistence: boolean;
  enabled: boolean;
};
export type Chat = {
  unreadCount: number;
  conversation: Conversation;
  contact: Contact;
  channel: Channel;
};
export type Message = {
  id: string;
  sender: string;
  body: string;
  type: string;
  mediaId?: string;
  mediaName?: string;
  status: string;
  createdAt: string;
  receivedAt: string;
};
export type Activity = {
  id: string;
  body: string;
  actor: string;
  kind: string;
  createdAt: string;
  contactId?: string;
};
export type Member = { subject: string; name: string; role: string; disabled: boolean };
export type Opportunity = {
  hospitalQuote?: string;
  hospitalPurchaseId?: string;
  purchasePending?: boolean;
  paymentReference?: string;
  id: string;
  title: string;
  contactId: string;
  value: number;
  stage: string;
};
export type Company = { id: string; name: string; industry: string; email: string; phone: string };
export type Me = {
  subject: string;
  name: string;
  role: string;
  tenant: { id: string; name: string; timeZone: string; agentEnabled: boolean };
};
