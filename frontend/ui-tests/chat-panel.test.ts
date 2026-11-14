import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createElement as h } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { SWRConfig } from 'swr';
import { AgentTrail, ChatPanel, trailKey } from '../src/components/chat-panel.tsx';

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

// docs/agent-trail.md S1: the agent notice (criteria 3, 5) and the trail inside it (3, 4, 6).
const zone = {
  id: 't-1',
  name: 'Hospital Sintético',
  timeZone: 'America/El_Salvador',
  agentEnabled: true,
};
function notice(chat: object, tenant: object = {}) {
  const base = {
    unreadCount: 0,
    sendBlock: null,
    agentBlock: null,
    handoff: null,
    contact: { id: 'k-1', name: 'Paciente Sintético', phone: '50370000000' },
    channel: { id: 'ch-1', name: 'WhatsApp' },
  };
  const me = { subject: 'doc-1', name: 'Ana', role: 'agent', tenant: { ...zone, ...tenant } };
  const swr = { fallback: { '/conversations/c-1/messages': [] }, provider: () => new Map() };
  return renderToStaticMarkup(
    h(
      SWRConfig,
      { value: swr },
      h(ChatPanel, {
        chat: { ...base, ...chat },
        me,
        onClose() {},
        refresh() {},
      } as unknown as Parameters<typeof ChatPanel>[0]),
    ),
  );
}
const tomar = (html: string) => /<button[^>]*>Tomar conversación/.exec(html)?.[0] ?? '';
test('AT_5_an_agent_conversation_whose_number_is_off_does_not_claim_the_agent_attends_and_can_be_taken', () => {
  const html = notice({
    conversation: { id: 'c-1', status: 'agent' },
    agentBlock: { code: 'channel', reason: 'El número está desactivado.' },
  });
  assert.ok(!html.includes('El agente está atendiendo'));
  const badge = html.slice(html.indexOf('whitespace-nowrap'), html.indexOf('Tomar conversación'));
  assert.ok(
    badge.includes('Sin atender') && !badge.includes('Agente IA'),
    'insignia de la cabecera',
  );
  assert.ok(html.includes('El agente no podría responder: El número está desactivado.'));
  assert.ok(tomar(html) && !/\sdisabled=/.test(tomar(html)), tomar(html));
});
test('AT_3_the_handoff_notice_says_the_reason_and_the_hour_in_the_tenants_zone', () => {
  const html = notice({
    conversation: { id: 'c-1', status: 'human' },
    handoff: { reason: 'Pidió hablar con una persona', at: '2026-10-25T16:30:00Z' },
  });
  assert.ok(html.includes('Pidió hablar con una persona'));
  assert.ok(html.includes('10:30'), 'hora de El Salvador (UTC-6)');
});
function trail(items: object[], extra: Record<string, unknown> = {}) {
  const fallback = {
    '/activity-feed?conversationId=c-1&page=1': {
      items,
      total: items.length,
      page: 1,
      pageSize: 30,
      careCounts: {},
    },
  };
  return renderToStaticMarkup(
    h(
      SWRConfig,
      { value: { fallback, provider: () => new Map() } },
      h(AgentTrail, { conversationId: 'c-1', zone: zone.timeZone, defaultOpen: true, ...extra }),
    ),
  );
}
const step = (o: object) => ({
  id: 's-1',
  kind: 'note',
  category: 'note',
  actor: 'Sistema',
  actorRole: 'system',
  careType: 'system',
  body: '',
  createdAt: '2026-10-25T16:30:00Z',
  ...o,
});
test('AT_3_a_handoff_step_shows_who_when_and_the_reason', () => {
  const html = trail([
    step({ kind: 'handoff', actor: 'Agente', body: 'Pidió hablar con una persona' }),
  ]);
  for (const text of ['Agente', '10:30', '25 de octubre de 2026', 'Pidió hablar con una persona'])
    assert.ok(html.includes(text), text);
});
test('AT_4_a_withheld_answer_reads_as_such_with_its_category_and_says_the_text_was_not_kept', () => {
  const html = trail([
    step({ kind: 'guard', category: 'review', body: 'Respuesta automática retenida: dosis.' }),
  ]);
  for (const text of ['Retuvo una respuesta automática', 'dosis', 'El texto retenido no se guardó'])
    assert.ok(html.includes(text), text);
});
test('AT_6_an_empty_trail_says_so_in_one_sentence', () => {
  assert.ok(trail([]).includes('El agente no hizo nada en esta conversación.'));
});
test('AT_closed_by_default_it_shows_no_steps_and_asks_for_nothing', () => {
  const html = renderToStaticMarkup(
    h(
      SWRConfig,
      { value: { provider: () => new Map() } },
      h(AgentTrail, { conversationId: 'c-1', zone: zone.timeZone }),
    ),
  );
  assert.ok(html.includes('aria-expanded="false"'));
  assert.ok(!html.includes('El agente no hizo nada'));
});

test('AT_closed_the_trail_asks_for_nothing_the_key_is_null_and_open_it_names_the_conversation', () => {
  assert.equal(trailKey(false, 'c-1', 1), null);
  assert.equal(trailKey(true, 'c-1', 2), '/activity-feed?conversationId=c-1&page=2');
  assert.ok(!String(trailKey(true, 'c-1', 1)).includes('contactId'));
});
