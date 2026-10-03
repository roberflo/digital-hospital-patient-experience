export type Conversation = {
  id: string;
  phone_number: string | null;
  phone_number_id: string;
  username?: string | null;
  business_scoped_user_id?: string | null;
  status: 'active' | 'ended';
  last_active_at?: string;
  kapso?: {
    contact_name?: string | null;
    messages_count?: number;
    last_message_text?: string;
    last_message_type?: string;
    last_message_timestamp?: string;
    last_inbound_at?: string | null;
    last_outbound_at?: string | null;
  };
};
export type MessageStatus = 'pending' | 'sent' | 'delivered' | 'read' | 'failed';
export type Message = {
  id: string;
  timestamp: string;
  type: string;
  text?: { body: string };
  kapso?: {
    direction?: 'inbound' | 'outbound';
    status?: MessageStatus;
    content?: string;
    whatsapp_conversation_id?: string;
    phone_number?: string;
    contact_name?: string;
    has_media?: boolean;
  };
};
export type Page<T> = {
  data: T[];
  paging?: {
    cursors?: { before?: string | null; after?: string | null };
    next?: string | null;
    previous?: string | null;
  };
};
export type InboxEvent = {
  event: string;
  phoneNumberId: string;
  conversationId?: string;
  payload: { message?: Message; conversation?: Conversation; [key: string]: unknown };
};
export function windowOpen(conversation: Conversation, now = Date.now()) {
  const value = conversation.kapso?.last_inbound_at;
  const time = value ? Date.parse(value) : NaN;
  return Number.isFinite(time) && time <= now && now - time < 24 * 60 * 60 * 1000;
}
export function mergeMessages(previous: Message[], incoming: Message[]) {
  const rank: Record<MessageStatus, number> = {
    pending: 0,
    sent: 1,
    delivered: 2,
    read: 3,
    failed: 4,
  };
  const all = new Map(previous.map((m) => [m.id, m]));
  for (const message of incoming) {
    const old = all.get(message.id);
    const oldStatus = old?.kapso?.status;
    const newStatus = message.kapso?.status;
    all.set(message.id, {
      ...old,
      ...message,
      kapso: {
        ...old?.kapso,
        ...message.kapso,
        ...(oldStatus && (!newStatus || rank[oldStatus] > rank[newStatus])
          ? { status: oldStatus }
          : {}),
      },
    });
  }
  return [...all.values()].sort((a, b) => Number(a.timestamp) - Number(b.timestamp));
}
