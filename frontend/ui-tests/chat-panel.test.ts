import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createElement as h } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { SWRConfig } from 'swr';
import { ChatPanel } from '../src/components/chat-panel.tsx';

// RXE-C1/C2/C4 on the real inbox bubble: the action is where the person reads the message, and only there.
const LABEL = 'Guardar en el expediente';
type Over = { sender?: string; type?: string; patientId?: string; role?: string; saved?: string };
function render({
  sender = 'patient',
  type = 'image',
  patientId = 'p-1',
  role = 'doctor',
  saved,
}: Over = {}) {
  const conversation = { id: 'c-1', status: 'human', assignedTo: 'doc-1' };
  const contact = { id: 'k-1', name: 'Paciente Sintético', phone: '50370000000', patientId };
  const chat = {
    unreadCount: 0,
    sendBlock: null,
    agentBlock: null,
    handoff: null,
    conversation,
    contact,
    channel: { id: 'ch-1', name: 'WhatsApp' },
  };
  const me = {
    subject: 'doc-1',
    name: 'Dra. Sintética',
    role,
    tenant: {
      id: 't-1',
      name: 'Hospital Sintético',
      timeZone: 'America/El_Salvador',
      agentEnabled: true,
    },
  };
  const message = {
    id: 'm-1',
    sender,
    body: '[imagen]',
    type,
    mediaId: type === 'text' ? undefined : 'media-1',
    hospitalAttachmentId: saved,
    status: 'received',
    createdAt: '2026-10-25T10:00:00Z',
    receivedAt: '2026-10-25T10:00:00Z',
  };
  const swr = { fallback: { '/conversations/c-1/messages': [message] }, provider: () => new Map() };
  // Synthetic fixtures with only the fields the bubble reads.
  return renderToStaticMarkup(
    h(
      SWRConfig,
      { value: swr },
      h(ChatPanel, { chat, me, onClose() {}, refresh() {} } as unknown as Parameters<
        typeof ChatPanel
      >[0]),
    ),
  );
}
test('RXE_C1_the_inbox_offers_it_on_an_inbound_image_of_a_linked_contact_to_a_doctor', () => {
  assert.ok(render().includes(LABEL));
});
test('RXE_C1_the_inbox_does_not_offer_it_otherwise', () => {
  for (const over of [
    { sender: 'human' },
    { sender: 'agent' },
    { type: 'text' },
    { type: 'audio' },
    { patientId: '' },
    { role: 'agent' },
    { role: 'admin' },
  ] as Over[])
    assert.ok(!render(over).includes(LABEL), JSON.stringify(over));
});
test('RXE_C4_a_saved_message_shows_it_is_in_the_record_instead_of_the_action', () => {
  const html = render({ saved: 'a-1' });
  assert.ok(html.includes('Guardado en el expediente'));
  assert.ok(!html.includes(LABEL));
});
