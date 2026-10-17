import { test } from 'node:test';
import assert from 'node:assert/strict';
import { defaultInboxFilters, filtersActive, inboxQuery } from '../src/lib/inbox-filters.ts';

// MR-18: el filtro de modo (personas/agente) lo resuelve el servidor, no la página cargada.
test('the attention mode travels as status=', () => {
  const query = inboxQuery({ ...defaultInboxFilters, mode: 'human' }, 2);
  assert.equal(query.get('status'), 'human');
  assert.equal(query.get('state'), 'open');
  assert.equal(query.get('page'), '2');
  assert.equal(inboxQuery(defaultInboxFilters, 1).has('status'), false);
});
test('empty secondary filters are not sent and a label is trimmed', () => {
  const query = inboxQuery({ ...defaultInboxFilters, state: '', label: '  cita ' }, 1);
  assert.equal(query.has('state'), false);
  assert.equal(query.has('channelId'), false);
  assert.equal(query.get('label'), 'cita');
});
test('only a departure from the default view counts as filtering', () => {
  assert.equal(filtersActive(defaultInboxFilters, ''), false);
  assert.equal(filtersActive({ ...defaultInboxFilters, mode: 'agent' }, ''), true);
  assert.equal(filtersActive({ ...defaultInboxFilters, label: ' ' }, ''), false);
  assert.equal(filtersActive(defaultInboxFilters, 'Ana'), true);
});
