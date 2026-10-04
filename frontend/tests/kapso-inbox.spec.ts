import { test, expect } from '@playwright/test';
import type { Page } from '@playwright/test';
import { enter, login as signIn } from './login';
const id = '11111111-1111-4111-8111-111111111111';
const second = '22222222-2222-4222-8222-222222222222';
const base = {
  id,
  phone_number: '+50370000000',
  phone_number_id: '613008901896177',
  status: 'active',
  last_active_at: new Date().toISOString(),
  kapso: {
    contact_name: 'Paciente de prueba',
    last_inbound_at: new Date().toISOString(),
    last_message_text: 'Consulta de horario',
  },
};
const inbound = {
  id: 'wamid.in',
  timestamp: '1700000000',
  type: 'text',
  text: { body: 'Consulta de horario' },
  kapso: { direction: 'inbound' },
};
async function login(page: Page) {
  await page.route('**/api/crm/channels', (route) =>
    route.fulfill({
      json: [
        { id: 'fixture-channel', name: 'WhatsApp de prueba', phoneNumberId: base.phone_number_id },
      ],
    }),
  );
  await signIn(page);
}
async function realtime(page: Page) {
  await page.addInitScript(() => {
    Object.defineProperty(window, 'EventSource', {
      value: class {
        onopen: (() => void) | null = null;
        onmessage: ((event: { data: string }) => void) | null = null;
        onerror = null;
        handler = (event: Event) =>
          this.onmessage?.({ data: JSON.stringify((event as CustomEvent).detail) });
        constructor() {
          window.addEventListener('kapso-test-event', this.handler);
          setTimeout(() => this.onopen?.(), 20);
        }
        close() {
          window.removeEventListener('kapso-test-event', this.handler);
        }
      },
    });
  });
}
test('provider inbox rejects unauthenticated access', async ({ page, request }) => {
  for (const path of [
    '/api/conversations',
    '/api/kapso/stream',
    `/api/conversations/${id}/messages`,
  ]) {
    const result = await request.get(path);
    expect(result.status()).toBe(401);
  }
  await page.goto('/inbox');
  await expect(page).toHaveURL(/login/);
  const result = await request.post('/api/kapso/webhook', {
    data: { phone_number_id: base.phone_number_id },
  });
  expect(result.status()).toBe(401);
});
test('history supports nullable phones, older pages, search, filters and mobile back', async ({
  page,
}, info) => {
  await realtime(page);
  await login(page);
  const nullable = {
    ...base,
    id: second,
    phone_number: null,
    username: 'usuario-sin-telefono',
    status: 'ended',
    kapso: {},
  };
  await page.route('**/api/conversations?*', (route) =>
    route.fulfill({ json: { data: [base, nullable], manualSendEnabled: true } }),
  );
  await page.route('**/api/conversations/*/messages*', (route) => {
    const old = route.request().url().includes('cursor=');
    const isNull = route.request().url().includes(second);
    return route.fulfill({
      json: {
        conversation: isNull ? nullable : base,
        data: old
          ? [
              {
                ...inbound,
                id: 'wamid.old',
                timestamp: '1600000000',
                text: { body: 'Mensaje anterior' },
              },
            ]
          : [inbound],
        paging: old ? {} : { next: 'next-page', cursors: { after: 'older' } },
      },
    });
  });
  await page.goto('/inbox');
  await page.getByRole('button', { name: 'Finalizadas', exact: true }).click();
  await expect(
    page.getByRole('button', { name: 'Abrir conversación con Paciente de prueba' }),
  ).toHaveCount(0);
  await page.getByRole('button', { name: 'Abrir conversación con usuario-sin-telefono' }).click();
  await expect(page.getByLabel('Mensaje', { exact: true })).toBeDisabled();
  await expect(
    page.getByText('Este contacto no tiene teléfono disponible para enviar texto.'),
  ).toBeVisible();
  if (info.project.name === 'mobile')
    await page.getByRole('button', { name: 'Volver a conversaciones' }).click();
  await page.getByRole('button', { name: 'Todas', exact: true }).click();
  await page.getByLabel('Buscar conversación').fill('Paciente');
  await page.getByRole('button', { name: 'Abrir conversación con Paciente de prueba' }).click();
  await page.getByRole('button', { name: 'Cargar anteriores' }).click();
  await expect(page.getByText('Mensaje anterior', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Cargar anteriores' })).toHaveCount(0);
  await expect(page.getByLabel('Mensaje', { exact: true })).toBeEnabled();
  await expect(page.getByRole('heading', { name: 'Dashboard', exact: true })).toHaveCount(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({
    path: `../artifacts/kapso-inbox-${info.project.name}.png`,
    fullPage: true,
  });
});
test('optimistic send reconciles SSE and ACK without duplicate or regressed checks', async ({
  page,
}, info) => {
  await realtime(page);
  await login(page);
  await page.route('**/api/conversations?*', (route) =>
    route.fulfill({ json: { data: [base], manualSendEnabled: true } }),
  );
  await page.route('**/api/conversations/*/messages*', (route) =>
    route.fulfill({ json: { conversation: base, data: [inbound] } }),
  );
  const sent = {
    ...inbound,
    id: 'wamid.out',
    text: { body: 'Estamos atendiendo\nhasta las cinco' },
    kapso: { direction: 'outbound', status: 'sent' },
  };
  await page.route('**/api/messages?*', async (route) => {
    expect(route.request().postDataJSON()).toEqual({
      conversationId: id,
      to: base.phone_number,
      text: sent.text.body,
    });
    await page.evaluate(
      (event) => window.dispatchEvent(new CustomEvent('kapso-test-event', { detail: event })),
      {
        event: 'whatsapp.message.read',
        phoneNumberId: base.phone_number_id,
        conversationId: id,
        payload: { message: { ...sent, kapso: { ...sent.kapso, status: 'read' } } },
      },
    );
    await route.fulfill({ json: { message: sent } });
  });
  await page.goto('/inbox');
  await page.getByRole('button', { name: 'Abrir conversación con Paciente de prueba' }).click();
  const composer = page.getByLabel('Mensaje', { exact: true });
  await composer.fill('Estamos atendiendo');
  await composer.press('Shift+Enter');
  await composer.pressSequentially('hasta las cinco');
  await composer.press('Enter');
  await expect(page.getByLabel('Leído', { exact: true })).toBeVisible();
  await expect(page.getByText(sent.text.body, { exact: true })).toHaveCount(1);
  await expect(composer).toHaveValue('');
  await page.screenshot({
    path: `../artifacts/kapso-send-${info.project.name}.png`,
    fullPage: true,
  });
});
test('closed 24h window blocks composer even when manual send is configured', async ({ page }) => {
  await realtime(page);
  await login(page);
  const expired = { ...base, kapso: { ...base.kapso, last_inbound_at: '2020-01-01T00:00:00Z' } };
  await page.route('**/api/conversations?*', (route) =>
    route.fulfill({ json: { data: [expired], manualSendEnabled: true } }),
  );
  await page.route('**/api/conversations/*/messages*', (route) =>
    route.fulfill({ json: { data: [], conversation: expired } }),
  );
  await page.goto('/inbox');
  await page.getByRole('button', { name: 'Abrir conversación con Paciente de prueba' }).click();
  await expect(page.getByText('Ventana de 24h cerrada: usa una plantilla')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Enviar mensaje' })).toBeDisabled();
});

test('incoming SSE events add one unread badge and deduplicate retries', async ({ page }) => {
  await realtime(page);
  await login(page);
  await page.route('**/api/conversations?*', (route) =>
    route.fulfill({ json: { data: [base], manualSendEnabled: true } }),
  );
  await page.route('**/api/conversations/*/messages*', (route) =>
    route.fulfill({ json: { conversation: base, data: [inbound] } }),
  );
  await page.goto('/inbox');
  await expect(
    page.getByRole('button', { name: 'Abrir conversación con Paciente de prueba' }),
  ).toBeVisible();
  const event = {
    event: 'whatsapp.message.received',
    phoneNumberId: base.phone_number_id,
    conversationId: id,
    payload: { message: inbound, conversation: base },
  };
  await page.evaluate((event) => {
    window.dispatchEvent(new CustomEvent('kapso-test-event', { detail: event }));
    window.dispatchEvent(new CustomEvent('kapso-test-event', { detail: event }));
  }, event);
  await expect(page.getByLabel('1 no leídos')).toBeVisible();
  await page.getByRole('button', { name: 'Abrir conversación con Paciente de prueba' }).click();
  await expect(page.getByLabel('1 no leídos')).toHaveCount(0);
  await expect(page.getByText('Consulta de horario', { exact: true }).last()).toBeVisible();
});

test('list pagination retains its cursor across fallback polling', async ({ page }) => {
  await realtime(page);
  await login(page);
  const cursors: (string | null)[] = [];
  await page.route('**/api/conversations*', (route) => {
    const after = new URL(route.request().url()).searchParams.get('after');
    cursors.push(after);
    return route.fulfill({
      json: {
        data: [{ ...base, id: after === 'second' ? second : id }],
        manualSendEnabled: true,
        paging: { next: 'page', cursors: { after: after === 'second' ? 'third' : 'second' } },
      },
    });
  });
  await page.goto('/inbox');
  await page.getByRole('button', { name: 'Cargar más conversaciones' }).click();
  await expect.poll(() => cursors.includes('second')).toBe(true);
  await page.clock.install();
  await page.clock.fastForward(16000);
  await page.getByRole('button', { name: 'Cargar más conversaciones' }).click();
  await expect.poll(() => cursors.includes('third')).toBe(true);
});

test('switching hospital number clears the open chat and scopes provider requests', async ({
  page,
}) => {
  await realtime(page);
  await login(page);
  await page.route('**/api/crm/channels', (route) =>
    route.fulfill({
      json: [
        { id: 'one', name: 'Recepción general', phoneNumberId: base.phone_number_id },
        { id: 'two', name: 'Doctor de prueba', phoneNumberId: '1234567890' },
      ],
    }),
  );
  await page.route('**/api/conversations?*', (route) => {
    const number = new URL(route.request().url()).searchParams.get('phoneNumberId');
    return route.fulfill({
      json: {
        data:
          number === base.phone_number_id
            ? [base]
            : [
                {
                  ...base,
                  id: second,
                  phone_number_id: number,
                  kapso: { contact_name: 'Paciente del doctor' },
                },
              ],
        manualSendEnabled: true,
      },
    });
  });
  await page.route('**/api/conversations/*/messages*', (route) =>
    route.fulfill({ json: { conversation: base, data: [inbound] } }),
  );
  await page.goto('/inbox');
  await page.getByRole('button', { name: 'Abrir conversación con Paciente de prueba' }).click();
  await expect(page.getByRole('heading', { name: 'Paciente de prueba' })).toBeVisible();
  if (test.info().project.name === 'mobile')
    await page.getByRole('button', { name: 'Volver a conversaciones' }).click();
  await page.getByLabel('Número de WhatsApp', { exact: true }).selectOption('1234567890');
  await expect(
    page.getByRole('button', { name: 'Abrir conversación con Paciente del doctor' }),
  ).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Paciente de prueba' })).toHaveCount(0);
});

test('expired manual send restores WhatsApp draft and conversation after signing in', async ({
  page,
  context,
}) => {
  await realtime(page);
  await login(page);
  await page.route('**/api/conversations?*', (route) =>
    route.fulfill({ json: { data: [base], manualSendEnabled: true } }),
  );
  await page.route('**/api/conversations/*/messages*', (route) =>
    route.fulfill({ json: { conversation: base, data: [inbound] } }),
  );
  let attempts = 0;
  await page.route('**/api/messages?*', async (route) => {
    attempts++;
    await context.clearCookies();
    return route.fulfill({ status: 401, json: { error: 'Sesión expirada' } });
  });
  await page.goto('/inbox');
  await page.getByRole('button', { name: 'Abrir conversación con Paciente de prueba' }).click();
  await page.getByLabel('Mensaje', { exact: true }).fill('Borrador WhatsApp sintético');
  await page.getByRole('button', { name: 'Enviar mensaje', exact: true }).click();
  const dialog = page.getByRole('dialog').filter({ hasText: 'Tu sesión terminó' });
  await expect(dialog).toBeVisible();
  const popup = context.waitForEvent('page');
  await dialog.getByRole('link', { name: 'Iniciar sesión y continuar' }).click();
  const auth = await popup;
  await enter(auth);
  await expect(auth.getByRole('heading', { name: 'Ya puedes continuar' })).toBeVisible();
  await expect(dialog).not.toBeVisible();
  await expect(page.getByLabel('Mensaje', { exact: true })).toHaveValue(
    'Borrador WhatsApp sintético',
  );
  await expect(
    page.getByRole('heading', { name: 'Paciente de prueba', exact: true }),
  ).toBeVisible();
  expect(attempts).toBe(1);
  await page.unrouteAll({ behavior: 'wait' });
  await auth.close();
});
