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
