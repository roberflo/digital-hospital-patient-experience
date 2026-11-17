import { test, expect, type Page, type Request } from '@playwright/test';
import { login } from './login';
// Lote F (docs/mejoras-revision-2026-10.md, MR-46…MR-58): the WhatsApp inbox, reviewed in the browser
// on 2026-10-25. The API is simulated with page.route in the shapes GET /api/conversations sends
// (`InboxRow`: sendBlock, agentBlock and handoff are always present, null when they do not apply);
// the session is the real Keycloak login.
test.afterEach(async ({ page }) => {
  await page.unrouteAll({ behavior: 'wait' });
});
const channel = {
  id: 'b3c1d2e4-0000-4000-8000-000000000046',
  name: 'WhatsApp principal',
  phoneNumberId: '1234567890',
  coexistence: false,
  enabled: true,
};
type Block = { code: 'channel' | 'window' | 'paused'; reason: string } | null;
function chat(
  name: string,
  conversation: Record<string, unknown> = {},
  row: { sendBlock?: Block; agentBlock?: Block; handoff?: { reason: string; at: string } } = {},
) {
  const contact = {
    id: crypto.randomUUID(),
    name,
    phone: '5037' + Math.floor(1000000 + Math.random() * 8999999),
    email: '',
    tags: '',
    lifecycleStage: 'lead',
  };
  return {
    unreadCount: 0,
    contact,
    channel,
    sendBlock: null as Block,
    agentBlock: null as Block,
    handoff: null as { reason: string; at: string } | null,
    ...row,
    conversation: {
      id: crypto.randomUUID(),
      contactId: contact.id,
      channelId: channel.id,
      status: 'human',
      state: 'open',
      priority: 'normal',
      labels: '',
      revision: 1,
      summary: '',
      lastMessage: 'Hola',
      updatedAt: '2026-10-25T15:00:00Z',
      lastInboundAt: new Date().toISOString(),
      ...conversation,
    },
  };
}
type Row = ReturnType<typeof chat>;
const isList = (r: Request) => new URL(r.url()).pathname === '/api/crm/conversations';
/** Serves the inbox page from `rows(params)`, as the API would answer those filters, and records every list request. */
async function serveInbox(
  page: Page,
  rows: (params: URLSearchParams) => Row[],
  labels: string[] = [],
) {
  const seen: Request[] = [];
  await page.route('**/api/crm/conversations?*', (route) => {
    seen.push(route.request());
    const params = new URL(route.request().url()).searchParams;
    const id = params.get('conversationId');
    const all = rows(params);
    return route.fulfill({ json: id ? all.filter((c) => c.conversation.id === id) : all });
  });
  await page.route('**/api/crm/conversations/labels', (route) => route.fulfill({ json: labels }));
  await page.route('**/api/crm/conversations/*/messages', (route) =>
    route.request().method() === 'GET' ? route.fulfill({ json: [] }) : route.fallback(),
  );
  await page.route('**/api/crm/activities?*', (route) => route.fulfill({ json: [] }));
  return seen;
}
async function agentSwitch(page: Page, agentEnabled: boolean) {
  await page.route('**/api/crm/me', async (route) => {
    const me = await (await route.fetch()).json();
    return route.fulfill({ json: { ...me, tenant: { ...me.tenant, agentEnabled } } });
  });
}
async function openInbox(page: Page) {
  await login(page);
  await page.goto('/?view=inbox');
}
const card = (page: Page, name: string) =>
  page.locator('.conversation-card').filter({ hasText: name });
const header = (page: Page) => page.locator('[data-chat-open] > div').first();
/** Below 1280 px the patient context is a sheet: open it. */
async function context(page: Page, control: ReturnType<Page['getByRole']>) {
  if (!(await control.isVisible()))
    await page.getByRole('button', { name: 'Ver información del paciente' }).click();
}

test('MR-46 with the agent off an agent conversation reads «Sin atender», never «Agente IA»', async ({
  page,
}) => {
  await serveInbox(page, () => [chat('Ana Sintética', { status: 'agent' })]);
  await agentSwitch(page, false);
  await openInbox(page);
  await expect(card(page, 'Ana Sintética').getByText('Sin atender', { exact: true })).toBeVisible();
  await expect(card(page, 'Ana Sintética').getByText('Agente IA')).toHaveCount(0);
  await card(page, 'Ana Sintética').click();
  // The chat header, also on a phone (MR-56): who attends is visible.
  await expect(header(page).getByText('Sin atender', { exact: true })).toBeVisible();
  await expect(header(page).getByText('Agente IA')).toHaveCount(0);
});

test('MR-46 with the agent on the same conversation still reads «Agente IA»', async ({ page }) => {
  await serveInbox(page, () => [chat('Ana Sintética', { status: 'agent' })]);
  await agentSwitch(page, true);
  await openInbox(page);
  await expect(card(page, 'Ana Sintética').getByText('Agente IA', { exact: true })).toBeVisible();
  await expect(card(page, 'Ana Sintética').getByText('Sin atender')).toHaveCount(0);
});

test('MR-47 a handoff shows «Transferida» and its reason on the card and in the chat', async ({
  page,
}) => {
  const reason = 'Pregunta por un cambio de dosis';
  await serveInbox(page, () => [
    chat(
      'Ana Sintética',
      { priority: 'urgent', lastMessage: 'Último mensaje sintético', summary: reason },
      { handoff: { reason, at: '2026-10-25T15:00:00Z' } },
    ),
  ]);
  await openInbox(page);
  const ana = card(page, 'Ana Sintética');
  await expect(ana.getByText('Transferida', { exact: true })).toBeVisible();
  await expect(ana.getByText('Motivo: ' + reason, { exact: true })).toBeVisible();
  await expect(ana.getByText('Urgente', { exact: true })).toBeVisible();
  await ana.click();
  await expect(page.getByText('Transferida por el agente', { exact: true })).toBeVisible();
  await expect(page.locator('[data-chat-open]').getByText(reason, { exact: true })).toBeVisible();
});

test('MR-48 a closed window is said before typing and «Enviar» stays disabled', async ({
  page,
}) => {
  const reason =
    'Ventana de 24 horas cerrada: WhatsApp solo permite enviar plantillas aprobadas hasta que el paciente vuelva a escribir.';
  const ana = chat('Ana Sintética', {}, { sendBlock: { code: 'window', reason } });
  await serveInbox(page, () => [ana]);
  const sends: unknown[] = [];
  await page.route(`**/api/crm/conversations/${ana.conversation.id}/messages`, (route) => {
    if (route.request().method() === 'POST') sends.push(route.request().postData());
    return route.fallback();
  });
  await openInbox(page);
  await card(page, 'Ana Sintética').click();
  await expect(
    page.getByText('Solo se pueden enviar plantillas aprobadas', { exact: true }),
  ).toBeVisible();
  await expect(page.getByText(reason, { exact: true })).toBeVisible();
  await page.getByLabel('Mensaje al paciente').fill('Hola');
  const send = page.getByRole('button', { name: 'Enviar', exact: true });
  await expect(send).toBeDisabled();
  await expect(send).toHaveAccessibleDescription(/Ventana de 24 horas cerrada/);
  await expect(page.locator('label[title="Adjuntar archivo"] input')).toBeDisabled();
  await expect(page.getByText(/Kapso/)).toHaveCount(0);
  expect(sends).toHaveLength(0);
});

test('MR-49 «Activar agente» says nobody will be responsible, and is off when the agent cannot send', async ({
  page,
  isMobile,
}) => {
  const reason = 'Este número está desactivado. Un administrador puede activarlo en WhatsApp.';
  const free = chat('Ana Sintética');
  const blocked = chat('Bruno Sintético', {}, { agentBlock: { code: 'channel', reason } });
  await serveInbox(page, () => [free, blocked]);
  await agentSwitch(page, true);
  const patches: unknown[] = [];
  await page.route('**/api/crm/conversations/*', (route) => {
    if (route.request().method() !== 'PATCH') return route.fallback();
    patches.push(route.request().postDataJSON());
    return route.fulfill({ json: free.conversation });
  });
  await openInbox(page);
  await card(page, 'Ana Sintética').click();
  await page.getByRole('button', { name: 'Activar agente' }).click();
  await expect(page.getByRole('alertdialog')).toContainText(
    'La conversación quedará sin responsable',
  );
  await page.getByRole('alertdialog').getByRole('button', { name: 'Cancelar' }).click();
  expect(patches).toHaveLength(0);
  await expect(page.getByRole('alertdialog')).toHaveCount(0);
  if (isMobile) await page.getByRole('button', { name: 'Volver a conversaciones' }).click();
  await card(page, 'Bruno Sintético').click();
  await expect(page.getByRole('button', { name: 'Activar agente' })).toBeDisabled();
  await expect(page.getByText('El agente no podría responder: ' + reason)).toBeVisible();
});

test('MR-50 a conversation whose state leaves the filter stays open', async ({ page }) => {
  const ana = chat('Ana Sintética');
  let state = 'open';
  // The list follows the state filter; asked for by id the API answers in any state.
  await serveInbox(page, (params) => {
    const row = { ...ana, conversation: { ...ana.conversation, state } };
    const filter = params.get('state');
    return params.get('conversationId') || !filter || filter === state ? [row] : [];
  });
  await page.route('**/api/crm/conversations/*/workflow', (route) => {
    state = route.request().postDataJSON().state;
    return route.fulfill({ json: { ...ana.conversation, state } });
  });
  await openInbox(page);
  await card(page, 'Ana Sintética').click();
  const select = page.getByLabel('Estado de conversación', { exact: true });
  await context(page, select);
  await select.selectOption('pending');
  await expect.poll(() => state).toBe('pending');
  await expect(select).toHaveValue('pending');
  await expect(page.locator('[data-chat-open] h2')).toHaveText('Ana Sintética');
});

test('MR-51 postponing sets state and time together, and another delay reschedules', async ({
  page,
}) => {
  const ana = chat('Ana Sintética');
  await serveInbox(page, () => [ana]);
  const sent: { state: string; snoozedUntil: string }[] = [];
  await page.route('**/api/crm/conversations/*/workflow', (route) => {
    sent.push(route.request().postDataJSON());
    return route.fulfill({ json: ana.conversation });
  });
  await openInbox(page);
  await card(page, 'Ana Sintética').click();
  const day = page.getByRole('button', { name: 'Posponer 1 día' });
  await context(page, day);
  await expect(
    page.getByLabel('Estado de conversación', { exact: true }).locator('option[value=snoozed]'),
  ).toBeDisabled();
  await day.click();
  await expect.poll(() => sent.length).toBe(1);
  await page.getByRole('button', { name: 'Posponer 1 semana' }).click();
  await expect.poll(() => sent.length).toBe(2);
  const hours = sent.map((b) => (Date.parse(b.snoozedUntil) - Date.now()) / 3600000);
  expect(sent.map((b) => b.state)).toEqual(['snoozed', 'snoozed']);
  expect(hours[0]).toBeGreaterThan(23);
  expect(hours[0]).toBeLessThanOrEqual(24);
  expect(hours[1]).toBeGreaterThan(167);
  expect(hours[1]).toBeLessThanOrEqual(168);
});

test('MR-52 cards show two labels and «+N»; the label filter lists the hospital’s labels', async ({
  page,
}) => {
  await serveInbox(page, () => [chat('Ana Sintética', { labels: 'cita, receta, urgente' })], [
    'cita',
    'receta',
    'urgente',
  ]);
  await openInbox(page);
  const ana = card(page, 'Ana Sintética');
  await expect(ana.getByText('cita', { exact: true })).toBeVisible();
  await expect(ana.getByText('receta', { exact: true })).toBeVisible();
  await expect(ana.getByText('+1', { exact: true })).toBeVisible();
  await expect(ana.getByText('urgente', { exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: /^Más filtros/ }).click();
  const filter = page.getByLabel('Filtrar por etiqueta');
  await expect(filter.locator('option')).toHaveText([
    'Todas las etiquetas',
    'cita',
    'receta',
    'urgente',
  ]);
  const labelled = page.waitForRequest(
    (r) => isList(r) && new URL(r.url()).searchParams.get('label') === 'receta',
  );
  await filter.selectOption('receta');
  await labelled;
});

test('MR-53 the inbox search is sent to the server as q=', async ({ page }) => {
  await serveInbox(page, () => [chat('Ana Sintética')]);
  await openInbox(page);
  const searched = page.waitForRequest(
    (r) => isList(r) && new URL(r.url()).searchParams.get('q') === 'receta',
  );
  await page.getByLabel('Buscar', { exact: true }).fill('receta');
  await searched;
  await expect(page.getByText(/solo revisa las 100 conversaciones/)).toHaveCount(0);
});

test('MR-54 the open conversation is in the address and a reload opens it again', async ({
  page,
}) => {
  const ana = chat('Ana Sintética');
  await serveInbox(page, () => [ana, chat('Bruno Sintético')]);
  await openInbox(page);
  await card(page, 'Ana Sintética').click();
  await expect(page).toHaveURL(new RegExp('conversationId=' + ana.conversation.id));
  await page.reload();
  await expect(page.locator('[data-chat-open] h2')).toHaveText('Ana Sintética');
});

test('MR-55 MR-58 nothing covers «Agente»; the context button only exists without a third column', async ({
  page,
  isMobile,
}) => {
  await serveInbox(page, () => [chat('Ana Sintética')]);
  await openInbox(page);
  const agent = await page.getByRole('button', { name: 'Agente', exact: true }).boundingBox();
  const more = await page.getByRole('button', { name: /^Más filtros/ }).boundingBox();
  expect(agent && more).toBeTruthy();
  const overlap =
    agent!.x < more!.x + more!.width &&
    more!.x < agent!.x + agent!.width &&
    agent!.y < more!.y + more!.height &&
    more!.y < agent!.y + agent!.height;
  expect(overlap).toBe(false);
  await card(page, 'Ana Sintética').click();
  // 1440 px (desktop project): the third column is there and the button would only duplicate it.
  await expect(page.getByRole('button', { name: 'Ver información del paciente' })).toHaveCount(
    isMobile ? 1 : 0,
  );
  await expect(page.getByRole('heading', { name: 'Expediente del hospital' }))[
    isMobile ? 'toBeHidden' : 'toBeVisible'
  ]();
});

test('MR-56 an open chat shows who attends; on a phone it takes the page title’s place', async ({
  page,
  isMobile,
}) => {
  await serveInbox(page, () => [chat('Ana Sintética')]);
  await openInbox(page);
  const title = page.getByRole('heading', { name: 'Bandeja de entrada', level: 1 });
  await expect(title).toBeVisible();
  await card(page, 'Ana Sintética').click();
  await expect(header(page).getByText('Atención humana', { exact: true })).toBeVisible();
  // A phone gives the chat the whole page; a desktop keeps title and search above it.
  await expect(title)[isMobile ? 'toBeHidden' : 'toBeVisible']();
  await expect(page.getByLabel('Buscar', { exact: true }))[
    isMobile ? 'toBeHidden' : 'toBeVisible'
  ]();
  if (isMobile) {
    await page.getByRole('button', { name: 'Volver a conversaciones' }).click();
    await expect(title).toBeVisible();
  }
});

// docs/agent-trail.md S1: what the agent did in this conversation, inside the attention notice.
// The feed is simulated with page.route in the shape of GET /api/activity-feed.
const trailStep = (n: number) => ({
  id: crypto.randomUUID(),
  kind: 'agent_tool',
  category: 'ai_action',
  actor: 'Agente',
  actorRole: 'agent_ai',
  careType: 'ai',
  body: `Consulta de disponibilidad: completado. (${n})`,
  createdAt: '2026-10-25T16:30:00Z',
});
const trailButton = (page: Page) => page.getByRole('button', { name: 'Qué hizo el agente aquí' });
async function serveTrail(
  page: Page,
  answer: (params: URLSearchParams) => { status?: number; json: object },
) {
  const seen: URLSearchParams[] = [];
  await page.route('**/api/crm/activity-feed?*', (route) => {
    const params = new URL(route.request().url()).searchParams;
    seen.push(params);
    const { status = 200, json } = answer(params);
    return route.fulfill({ status, json });
  });
  return seen;
}
const feedPage = (items: object[], total = items.length, pageNumber = 1) => ({
  json: { items, total, page: pageNumber, pageSize: 30, careCounts: {} },
});
async function openConversation(page: Page, row: Row) {
  await serveInbox(page, () => [row]);
  await openInbox(page);
  await card(page, row.contact.name).click();
}

test('AT-1 the trail asks for this conversation, never the contact, and only once opened', async ({
  page,
}) => {
  const row = chat('Ana Sintética', { status: 'agent' });
  const seen = await serveTrail(page, () => feedPage([trailStep(1)]));
  await openConversation(page, row);
  await expect(trailButton(page)).toHaveAttribute('aria-expanded', 'false');
  expect(seen).toHaveLength(0);
  await trailButton(page).click();
  await expect(page.getByText('Consulta de disponibilidad: completado. (1)')).toBeVisible();
  expect(seen[0].get('conversationId')).toBe(row.conversation.id);
  expect(seen[0].has('contactId')).toBe(false);
});

test('AT-7 a failing trail shows «Reintentar» and the patient can still be answered', async ({
  page,
}) => {
  let failing = true;
  await serveTrail(page, () =>
    failing ? { status: 500, json: { error: 'x' } } : feedPage([trailStep(1)]),
  );
  await openConversation(page, chat('Ana Sintética', { status: 'human', assignedTo: null }));
  await trailButton(page).click();
  const retry = page.getByRole('button', { name: 'Reintentar' });
  await expect(retry).toBeVisible();
  await expect(page.getByLabel('Mensaje al paciente')).toBeEnabled();
  failing = false;
  await retry.click();
  await expect(page.getByText('Consulta de disponibilidad: completado. (1)')).toBeVisible();
});

test('AT-8 with more than 30 steps the previous ones are one click away', async ({ page }) => {
  const seen = await serveTrail(page, (p) =>
    feedPage([trailStep(Number(p.get('page')))], 31, Number(p.get('page'))),
  );
  const row = chat('Ana Sintética', { status: 'agent' });
  await openConversation(page, row);
  await trailButton(page).click();
  await page
    .getByRole('navigation', { name: 'Páginas del rastro' })
    .getByRole('button', { name: 'Siguiente' })
    .click();
  await expect(page.getByText('Consulta de disponibilidad: completado. (2)')).toBeVisible();
  expect(seen.at(-1)?.get('page')).toBe('2');
  expect(seen.at(-1)?.get('conversationId')).toBe(row.conversation.id);
});

test('AT-9 the trail refreshes within 10 s only when open and on page 1', async ({ page }) => {
  // runFor, not fastForward: with fastForward the second poll never fires (measured), so the refresh was untestable.
  await page.clock.install();
  const seen = await serveTrail(page, (p) => feedPage([trailStep(1)], 31, Number(p.get('page'))));
  await openConversation(page, chat('Ana Sintética', { status: 'agent' }));
  await page.clock.runFor(11000);
  expect(seen).toHaveLength(0); // closed: nothing is read
  await trailButton(page).click();
  await expect.poll(() => seen.length).toBe(1);
  await page.clock.runFor(11000);
  await expect.poll(() => seen.length).toBe(2); // open on page 1: it refreshes by itself
  await page
    .getByRole('navigation', { name: 'Páginas del rastro' })
    .getByRole('button', { name: 'Siguiente' })
    .click();
  await expect.poll(() => seen.length).toBe(3);
  await page.clock.runFor(11000);
  await page.waitForTimeout(500);
  expect(seen).toHaveLength(3); // page 2: no polling
});

test('AT-10 the trail opens by keyboard, announces its state and fits 360 px', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 800 });
  await serveTrail(page, () => feedPage([trailStep(1)]));
  await openConversation(page, chat('Ana Sintética', { status: 'agent' }));
  await trailButton(page).focus();
  await page.keyboard.press('Enter');
  await expect(trailButton(page)).toHaveAttribute('aria-expanded', 'true');
  await expect(page.getByText('Consulta de disponibilidad: completado. (1)')).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.keyboard.press('Space');
  await expect(trailButton(page)).toHaveAttribute('aria-expanded', 'false');
  // Reduced motion: ui-tests/chat-panel.test.ts AT_10 (spinner motion-reduce:animate-none).
});

test('AT-5 a conversation of the agent whose number is off can be taken', async ({ page }) => {
  await agentSwitch(page, true); // MR-49: else «apagado» wins as the reason
  await serveTrail(page, () => feedPage([]));
  await openConversation(
    page,
    chat(
      'Ana Sintética',
      { status: 'agent' },
      { agentBlock: { code: 'channel', reason: 'El número está desactivado.' } },
    ),
  );
  await expect(
    page.getByText('El agente no podría responder: El número está desactivado.'),
  ).toBeVisible();
  await expect(page.getByText('El agente está atendiendo')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Tomar conversación' })).toBeEnabled();
  await expect(header(page).getByText('Sin atender', { exact: true })).toBeVisible();
});
