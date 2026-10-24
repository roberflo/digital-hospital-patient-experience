import { test } from 'node:test';
import assert from 'node:assert/strict';
import { OTHER_COUNTRY, phoneToSend, splitPhone } from '../src/lib/phone.ts';

// Criterio 29: selector de país, El Salvador +503 por defecto; se envía E.164 completo.
test('a new contact starts in El Salvador and is sent in E.164', () => {
  assert.deepEqual(splitPhone(), { country: '503', local: '' });
  assert.deepEqual(phoneToSend('503', '7000 0000'), { phone: '+50370000000' });
  assert.deepEqual(phoneToSend('504', '9999-9999'), { phone: '+50499999999' });
  assert.deepEqual(phoneToSend('502', '2345 6789'), { phone: '+50223456789' });
  assert.deepEqual(phoneToSend('1', '415 555 0100'), { phone: '+14155550100' });
  assert.deepEqual(phoneToSend(OTHER_COUNTRY, '+44 7700 900123'), { phone: '+447700900123' });
});

test('the length the country expects is asked for, without sending', () => {
  assert.match((phoneToSend('1', '7000 0000') as { error: string }).error, /10 dígitos/);
  assert.match((phoneToSend('503', '7000 000') as { error: string }).error, /8 dígitos/);
  assert.ok('error' in phoneToSend(OTHER_COUNTRY, '7000 0000')); // without «+»
  assert.ok('error' in phoneToSend(OTHER_COUNTRY, '+290 22345')); // 8 digits in all (criterio 30)
});

test('a stored phone opens in its country, or in «Otro»', () => {
  assert.deepEqual(splitPhone('50223456789'), { country: '502', local: '23456789' });
  assert.deepEqual(splitPhone('70000001'), { country: '503', local: '70000001' }); // old 8-digit contact
  assert.deepEqual(splitPhone('14155550100'), { country: '1', local: '4155550100' });
  assert.deepEqual(splitPhone('447700900123'), { country: OTHER_COUNTRY, local: '+447700900123' });
});

// Criterio 26: saved without touching it, the stored value goes back as it was, so the server keeps its hash.
test('an unchanged phone is sent back as stored', () => {
  for (const stored of ['70000001', '50370000001', '14155550100', '447700900123']) {
    const { country, local } = splitPhone(stored);
    assert.deepEqual(phoneToSend(country, local, stored), { phone: stored });
  }
  assert.deepEqual(phoneToSend('503', '7000 0002', '70000001'), { phone: '+50370000002' });
});
