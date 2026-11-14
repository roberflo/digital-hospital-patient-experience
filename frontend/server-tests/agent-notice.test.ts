import { test } from 'node:test';
import assert from 'node:assert/strict';
import { agentNotice } from '../src/lib/agent-notice.ts';

// docs/agent-trail.md criterio 5 / INV-4: «atendiendo» sólo si el servidor dejaría responder al agente.
const block = (code: 'channel' | 'window' | 'paused') => ({ code, reason: `motivo ${code}` });
const base = { status: 'agent', agentEnabled: true, agentBlock: null, role: 'agent' };

test('an agent conversation with nothing in the way is attended', () => {
  const n = agentNotice(base);
  assert.equal(n.text, 'El agente está atendiendo esta conversación.');
  assert.equal(n.badge, 'agent');
  assert.equal(n.canActivate, true);
});
test('an agent conversation whose number is off or paused says why and is unattended', () => {
  for (const code of ['channel', 'paused'] as const) {
    const n = agentNotice({ ...base, agentBlock: block(code) });
    assert.equal(n.text, `El agente no podría responder: motivo ${code}`);
    assert.equal(n.badge, 'unattended');
    assert.equal(n.canActivate, true); // «Tomar conversación» stays available
  }
});
test('the hospital agent off wins over a block and tells an admin from the rest', () => {
  const admin = agentNotice({
    ...base,
    agentEnabled: false,
    agentBlock: block('paused'),
    role: 'admin',
  });
  assert.match(admin.text, /apagado para todo el hospital\. Actívalo en Configuración/);
  assert.equal(admin.badge, 'unattended');
  assert.match(agentNotice({ ...base, agentEnabled: false }).text, /Pide al administrador/);
});
test('a human conversation: the block stops «Activar agente», the agent off too', () => {
  const human = { ...base, status: 'human' };
  assert.equal(agentNotice(human).text, 'Tu equipo está a cargo. El agente está en pausa.');
  assert.equal(agentNotice(human).badge, 'human');
  assert.equal(agentNotice(human).canActivate, true);
  const blocked = agentNotice({ ...human, agentBlock: block('window') });
  assert.equal(blocked.text, 'El agente no podría responder: motivo window');
  assert.equal(blocked.canActivate, false);
  assert.equal(agentNotice({ ...human, agentEnabled: false }).canActivate, false);
});
test('a closed conversation says so, unless the agent is off, which then wins and blocks «Activar agente»', () => {
  const n = agentNotice({ ...base, status: 'closed', agentEnabled: false });
  assert.equal(n.badge, 'closed');
  assert.equal(n.canActivate, false); // «Activar agente» would promise what the off switch forbids
  assert.match(n.text, /apagado para todo el hospital/); // the off switch wins as the reason
  assert.equal(agentNotice({ ...base, status: 'closed' }).text, 'La conversación está cerrada.');
  assert.equal(agentNotice({ ...base, status: 'closed' }).canActivate, true);
});
