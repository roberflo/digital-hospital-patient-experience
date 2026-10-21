import { test, expect } from '@playwright/test';
import { login } from './login';
test.afterEach(async ({ page }) => {
  await page.unrouteAll({ behavior: 'wait' });
});
test('admin configures reminder channel, pauses dispatch and sees template and queue states', async ({
  page,
}, info) => {
  const channel = '11111111-aaaa-4111-8111-111111111111';
  let enabled = false;
  await page.route('**/api/crm/channels', (r) =>
    r.fulfill({
      json: [
        {
          id: channel,
          name: 'Recepción general sintética',
          enabled: true,
          phoneNumberId: 'synthetic',
        },
      ],
    }),
  );
  await page.route('**/api/crm/appointment-reminders', (r) =>
    r.fulfill({
      json: {
        enabled,
        channelId: channel,
        sendEnabled: true,
        consentingContacts: 1,
        lastSyncAt: '2026-10-03T16:00:00Z',
        rows: [
          {
            id: 'one',
            patientName: 'Paciente de prueba',
            startsAt: '2026-10-05T16:00:00Z',
            dueAt: '2026-10-04T15:00:00Z',
            window: 'day_before',
            status: 'pending',
          },
          {
            id: 'two',
            patientName: 'Paciente de prueba',
            startsAt: '2026-10-05T16:00:00Z',
            dueAt: '2026-10-05T15:00:00Z',
            window: 'hour_before',
            status: 'uncertain',
            reason: 'Entrega sin confirmar. Revisa WhatsApp antes de volver a enviar.',
          },
        ],
      },
    }),
  );
  const saved: { enabled: boolean; channelId: string; dayTemplate?: string }[] = [];
  await page.route('**/api/crm/appointment-reminders/settings', async (r) => {
    const body = r.request().postDataJSON();
    expect(body.channelId).toBe(channel);
    enabled = body.enabled;
    saved.push(body);
    await r.fulfill({ json: {} });
  });
  await page.route('**/api/crm/appointment-reminders/templates', (r) =>
    r.fulfill({
      json: [
        { name: 'day', status: 'APPROVED', ready: true },
        { name: 'hour', status: 'PENDING', ready: false },
      ],
    }),
  );
  await login(page);
  await page.goto('/?view=agent');
  await expect(
    page.getByRole('heading', { name: 'Recordatorios de citas', exact: true }),
  ).toBeVisible();
  await expect(page.getByText('09:00 del día anterior', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Activar recordatorios', exact: true }).click();
  await expect(
    page.getByRole('button', { name: 'Pausar recordatorios', exact: true }),
  ).toBeVisible();
  await page.getByRole('button', { name: 'Comprobar plantillas', exact: true }).click();
  await expect(
    page.getByText('Una hora antes: Pendiente de aprobación', { exact: true }),
  ).toBeVisible();
  await expect(page.getByText('Entrega sin confirmar', { exact: true })).toBeVisible();
  await page.screenshot({
    path: `../artifacts/reminders-${info.project.name}.png`,
    fullPage: true,
  });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.getByRole('button', { name: 'Pausar recordatorios', exact: true }).click();
  await expect(
    page.getByRole('button', { name: 'Activar recordatorios', exact: true }),
  ).toBeVisible();
  // Criterio 13: the tenant still has v1; the administrator switches the day-before reminder to the template with buttons.
  expect(saved.every((body) => body.dayTemplate === undefined)).toBe(true);
  await page.getByRole('button', { name: 'Usar plantilla con botones', exact: true }).click();
  await expect.poll(() => saved.at(-1)?.dayTemplate).toBe('recepcion_cita_dia_anterior_v4');
  expect(saved.at(-1)?.enabled).toBe(false);
});
test('staff cannot administer reminders and other tenant contact authorization is hidden', async ({
  page,
}) => {
  await login(page, 'agent');
  expect((await page.request.get('/api/crm/appointment-reminders')).status()).toBe(403);
  expect(
    (
      await page.request.get(
        '/api/crm/appointment-reminders/contacts/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
      )
    ).status(),
  ).toBe(404);
  await page.goto('/?view=agent');
  await expect(
    page.getByRole('heading', { name: 'Agente de atención', exact: true, level: 1 }),
  ).toBeVisible();
  await expect(
    page.getByRole('button', { name: 'Activar recordatorios', exact: true }),
  ).toHaveCount(0);
});
test('reception sees tomorrow: who confirmed and whom to call, with nothing clinical', async ({
  page,
}) => {
  const conversation = '22222222-bbbb-4222-8222-222222222222';
  const row = (
    id: string,
    startsAt: string,
    contactName: string,
    status: string,
    deliveryStatus = 'read',
  ) => ({
    id,
    startsAt,
    contactName,
    status,
    deliveryStatus,
    conversationId: conversation,
    // Fields the server never sends (criterio 14); present here so a view that paints the whole row fails.
    clinicianName: 'Dra. Sintética Rivas',
    reason: 'Control',
  });
  await page.route('**/api/crm/appointment-reminders/tomorrow', (r) =>
    r.fulfill({
      json: [
        row('one', '2026-10-04T15:00:00Z', 'Paciente Confirmada Sintética', 'confirmed'),
        row('two', '2026-10-04T16:00:00Z', 'Paciente Callado Sintético', 'sent'),
        row('three', '2026-10-04T17:00:00Z', 'Paciente Incierto Sintético', 'uncertain'),
        row('four', '2026-10-04T18:00:00Z', 'Paciente Pendiente Sintético', 'pending'),
        row('five', '2026-10-04T19:00:00Z', 'Paciente Fallido Sintético', 'sent', 'failed'),
      ],
    }),
  );
  await login(page, 'agent');
  // The real endpoint answers reception (A11); page.route does not intercept page.request.
  expect((await page.request.get('/api/crm/appointment-reminders/tomorrow')).status()).toBe(200);
  await page.goto('/?view=agent');
  await expect(
    page.getByRole('heading', { name: 'Recordatorios de mañana', exact: true }),
  ).toBeVisible();
  const confirmed = page.getByRole('region', { name: 'Confirmaron' });
  const unanswered = page.getByRole('region', { name: 'Sin respuesta' });
  await expect(confirmed.getByText('Paciente Confirmada Sintética')).toBeVisible();
  await expect(confirmed.getByText('Paciente Callado Sintético')).toHaveCount(0);
  await expect(unanswered.getByText('Paciente Callado Sintético')).toBeVisible();
  await expect(unanswered.getByText('Paciente Confirmada Sintética')).toHaveCount(0);
  await expect(
    page.getByRole('region', { name: 'Revisar' }).getByText('Paciente Incierto Sintético'),
  ).toBeVisible();
  // M-2: not yet sent is its own group, and the total counts every row.
  await expect(page.getByText('5 citas', { exact: true })).toBeVisible();
  await expect(
    page.getByRole('region', { name: 'Aún no enviado' }).getByText('Paciente Pendiente Sintético'),
  ).toBeVisible();
  // M-3: a send that failed is to review, never «no answer».
  await expect(
    page.getByRole('region', { name: 'Revisar' }).getByText('Paciente Fallido Sintético'),
  ).toBeVisible();
  await expect(unanswered.getByText('Paciente Fallido Sintético')).toHaveCount(0);
  await expect(page.getByText(/no vendr/i)).toHaveCount(0);
  await expect(unanswered.getByRole('link', { name: /Abrir conversación/ })).toHaveAttribute(
    'href',
    `/?view=inbox&conversationId=${conversation}`,
  );
  await expect(page.getByText('Dra. Sintética Rivas')).toHaveCount(0);
  await expect(page.getByText('Control', { exact: true })).toHaveCount(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.setViewportSize({ width: 360, height: 800 });
  await expect(unanswered.getByText('Paciente Callado Sintético')).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});
test('a doctor cannot read tomorrow’s reminders', async ({ page }) => {
  await login(page, 'doctor');
  expect((await page.request.get('/api/crm/appointment-reminders/tomorrow')).status()).toBe(403);
});
