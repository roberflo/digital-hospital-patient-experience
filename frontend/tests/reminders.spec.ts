import { test, expect, type Page } from '@playwright/test';
async function login(page: Page, user = 'admin') {
  await page.goto('/login');
  await page.getByLabel('Usuario de demostración').selectOption(user);
  await page.getByLabel('Contraseña', { exact: true }).fill('demo-recepcion');
  await page.getByRole('button', { name: 'Entrar al espacio' }).click();
  await expect(page.getByRole('heading', { name: 'Dashboard', exact: true })).toBeVisible();
}
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
  await page.route('**/api/crm/appointment-reminders/settings', async (r) => {
    const body = r.request().postDataJSON();
    expect(body.channelId).toBe(channel);
    enabled = body.enabled;
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
    page.getByRole('heading', { name: 'Agente de atención', exact: true }),
  ).toBeVisible();
  await expect(
    page.getByRole('button', { name: 'Activar recordatorios', exact: true }),
  ).toHaveCount(0);
});
