import { test } from 'node:test';
import assert from 'node:assert/strict';
import { dayInZone, inZone, noShowLine, whatsAppPhone, zoneName } from '../src/lib/format.ts';

// MR-23: la hora la dicta la zona del hospital, no la del navegador del recepcionista.
const lateNight = '2026-10-17T03:30:00Z'; // 21:30 del 16 de octubre en San Salvador; 12:30 del 17 en Tokio
test('formats a time in the hospital zone with es-SV, whatever the browser zone', () => {
  const salvador = inZone(lateNight, 'America/El_Salvador', { hour: '2-digit', minute: '2-digit' });
  assert.match(salvador, /^09:30\s?p\.\s?m\.$/);
  assert.notEqual(
    salvador,
    inZone(lateNight, 'Asia/Tokyo', { hour: '2-digit', minute: '2-digit' }),
  );
  assert.equal(
    inZone(lateNight, 'America/El_Salvador', { day: 'numeric', month: 'short' }),
    '16 oct',
  );
});
test('accepts epoch milliseconds as Kapso timestamps arrive', () => {
  assert.equal(
    inZone(Date.parse(lateNight), 'America/El_Salvador', { day: 'numeric', month: 'long' }),
    '16 de octubre',
  );
});
test('groups messages by the hospital day, not the browser day', () => {
  assert.equal(dayInZone(lateNight, 'America/El_Salvador'), '2026-10-16');
  assert.equal(dayInZone(lateNight, 'Asia/Tokyo'), '2026-10-17');
});

// MR-44: los no-show del paciente vinculado, solo si los hay, con singular y plural.
test('says how many times the patient did not come, and how many in a row', () => {
  assert.equal(noShowLine({ noShows: 0, consecutiveNoShows: 0 }), null);
  assert.equal(noShowLine({ noShows: 1, consecutiveNoShows: 0 }), 'No asistió 1 vez');
  assert.equal(noShowLine({ noShows: 1, consecutiveNoShows: 1 }), 'No asistió 1 vez · 1 seguida');
  assert.equal(
    noShowLine({ noShows: 4, consecutiveNoShows: 2 }),
    'No asistió 4 veces · 2 seguidas',
  );
});

test('names the hospital zone as staff say it, never its IANA id', () => {
  assert.equal(zoneName('America/El_Salvador'), 'Hora de El Salvador');
  assert.doesNotMatch(zoneName('America/Guatemala'), /America\//);
  assert.equal(zoneName('No/Existe'), 'No/Existe');
});

// Criterio 27: el teléfono se muestra como se envía; un contacto antiguo de 8 dígitos es salvadoreño.
test('shows a contact phone as WhatsApp receives it', () => {
  assert.equal(whatsAppPhone('70000001'), '+50370000001');
  assert.equal(whatsAppPhone('50370000001'), '+50370000001');
  assert.equal(whatsAppPhone('14155550100'), '+14155550100');
});
