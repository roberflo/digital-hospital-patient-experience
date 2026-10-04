import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  agentStep,
  firstSteps,
  hospitalState,
  receptionProbe,
  whatsappStep,
  type FirstStepsInput,
  type Installation,
} from '../src/lib/first-steps.ts';

const admin = { role: 'admin', subject: 'me', tenant: { agentEnabled: false } };
const ready: Installation = {
  kapsoKey: true,
  webhookUrl: true,
  webhookSecret: true,
  agentModel: true,
  autoSend: true,
};
const installation = { data: ready };
const linked = { id: 'c1', phoneNumberId: '123', enabled: false };
const active = { ...linked, enabled: true };
const receiving = { receivesMessages: true, signatureMatches: true };
const fresh: FirstStepsInput = {
  me: admin,
  connection: { data: { configured: true } },
  check: { data: { connected: true } },
  installation,
  channels: { data: [] },
  reception: {},
  workload: { data: { members: [{ subject: 'me', disabled: false }] } },
};
const states = (input: FirstStepsInput) => firstSteps(input)?.map((s) => s.state);

test('a new administrator sees the four steps in order, only the verified one done', () => {
  assert.deepEqual(
    firstSteps(fresh)?.map((s) => s.id),
    ['hospital', 'whatsapp', 'team', 'agent'],
  );
  assert.deepEqual(states(fresh), ['done', 'pending', 'pending', 'pending']);
});

test('other roles never get the guide', () => {
  for (const role of ['agent', 'doctor', ''])
    assert.equal(firstSteps({ ...fresh, me: { ...admin, role } }), null);
});

test('the guide disappears once every step is done, and does not flash while reads load', () => {
  const complete: FirstStepsInput = {
    ...fresh,
    me: { ...admin, tenant: { agentEnabled: true } },
    channels: { data: [active] },
    reception: { data: receiving },
    workload: {
      data: {
        members: [
          { subject: 'me', disabled: false },
          { subject: 'ana', disabled: false },
        ],
      },
    },
  };
  assert.equal(firstSteps(complete), null);
  assert.equal(firstSteps({ ...complete, channels: {}, check: {}, installation: {} }), null);
});

test('a failed Hospital check is failed, never done, even with a stale successful result', () => {
  const failed = { data: { connected: true }, error: new Error('502') };
  assert.equal(hospitalState(fresh.connection, failed), 'failed');
  assert.equal(states({ ...fresh, check: failed })?.[0], 'failed');
  assert.equal(hospitalState(fresh.connection, { data: { connected: false } }), 'failed');
  // Retrying shows progress, not the old verdict.
  assert.equal(hospitalState(fresh.connection, { ...failed, validating: true }), 'loading');
  assert.equal(hospitalState({ data: { configured: false } }, {}), 'pending');
});

test('a read that did not answer is neither pending nor done', () => {
  const error = new Error('offline');
  assert.deepEqual(
    states({ ...fresh, connection: { error }, channels: { error }, workload: { error } }),
    ['unknown', 'unknown', 'unknown', 'pending'],
  );
  assert.deepEqual(states({ ...fresh, channels: {}, workload: {} }), [
    'done',
    'loading',
    'loading',
    'pending',
  ]);
  // Stale data never outranks the failed read.
  assert.equal(states({ ...fresh, channels: { data: [active], error } })?.[1], 'unknown');
  // Without the installation read, neither WhatsApp nor the agent can be judged.
  assert.deepEqual(states({ ...fresh, installation: { error } }), [
    'done',
    'unknown',
    'pending',
    'unknown',
  ]);
});

test('WhatsApp: a linked number is not done until it is activated and reception is ready', () => {
  const step = (channels: object[], reception = {}) =>
    whatsappStep(installation, { data: channels as never }, reception);
  assert.deepEqual(step([]), { id: 'whatsapp', state: 'pending', need: 'link' });
  // The seeded demo channel is not a connected number, even enabled.
  assert.equal(step([{ id: 'd', phoneNumberId: 'demo', enabled: true }]).need, 'link');
  assert.deepEqual(step([linked]), {
    id: 'whatsapp',
    state: 'pending',
    need: 'activate',
    channel: linked,
  });
  // Active, reception not yet answered / failed to answer / answered not ready.
  assert.equal(step([active]).state, 'loading');
  assert.equal(step([active], { error: new Error('502') }).state, 'unknown');
  for (const reception of [
    { receivesMessages: false, signatureMatches: true },
    { receivesMessages: true, signatureMatches: false },
  ]) {
    const s = step([active], { data: reception });
    assert.equal(s.state, 'pending');
    assert.equal(s.need, 'reception');
  }
  assert.equal(step([active], { data: receiving }).state, 'done');
  assert.equal(
    step([active], { data: receiving, error: new Error('502') }).state,
    'unknown',
    'stale ready result does not outrank a failed read',
  );
});

test('WhatsApp: a signed event already received proves reception without asking Kapso', () => {
  const proven = { ...active, lastWebhookAt: '2026-10-04T00:00:00Z' };
  assert.equal(receptionProbe([proven]), null);
  assert.equal(whatsappStep(installation, { data: [proven] }, {}).state, 'done');
  // Only an active channel is probed; an event on a paused channel proves nothing.
  assert.equal(receptionProbe([linked]), null);
  assert.equal(receptionProbe([active])?.id, 'c1');
  assert.equal(
    whatsappStep(installation, { data: [{ ...linked, lastWebhookAt: 'x' }] }, {}).need,
    'activate',
  );
});

test('an installation the operator has not configured is blocked, by setting name', () => {
  const bare = { data: { ...ready, webhookUrl: false, webhookSecret: false } };
  const whatsapp = whatsappStep(bare, { data: [active] }, { data: receiving });
  assert.equal(whatsapp.state, 'blocked');
  assert.deepEqual(whatsapp.missing, ['KAPSO_WEBHOOK_URL (https)', 'KAPSO_WEBHOOK_SECRET']);
  assert.deepEqual(
    whatsappStep({ data: { ...ready, kapsoKey: false } }, { data: [] }, {}).missing,
    ['KAPSO_API_KEY'],
  );
  // The agent switched on without a model provider or automatic sending is not done.
  const agent = agentStep(true, { data: { ...ready, agentModel: false, autoSend: false } });
  assert.equal(agent.state, 'blocked');
  assert.deepEqual(agent.missing, ['NVIDIA_API_KEY', 'SEND_ENABLED=true']);
  assert.equal(agentStep(false, installation).state, 'pending');
  assert.equal(agentStep(true, installation).state, 'done');
  assert.equal(agentStep(true, {}).state, 'loading');
});

test('a disabled teammate and oneself do not complete the team step', () => {
  assert.equal(
    states({
      ...fresh,
      workload: {
        data: {
          members: [
            { subject: 'me', disabled: false },
            { subject: 'ana', disabled: true },
          ],
        },
      },
    })?.[2],
    'pending',
  );
});
