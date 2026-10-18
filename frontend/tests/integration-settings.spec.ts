import { test, expect } from '@playwright/test';
import { login } from './login';

test('business selects Google calendars by name and never enters integration identifiers', async ({
  page,
}) => {
  let selected = 'shared-calendar';
  await page.route('**/api/crm/settings', (route) =>
    route.fulfill({
      json: {
        name: 'Hospital sintético',
        guide: '',
        timeZone: 'America/El_Salvador',
        agentEnabled: false,
        googleConnected: true,
        googleCalendarId: selected,
      },
    }),
  );
  await page.route('**/api/crm/google/calendars', (route) =>
    route.fulfill({
      json: [
        { id: 'shared-calendar', name: 'Agenda del hospital', primary: false },
        { id: 'primary-calendar', name: 'Agenda personal', primary: true },
      ],
    }),
  );
  await page.route('**/api/crm/google/calendar', (route) => {
    selected = route.request().postDataJSON().id;
    return route.fulfill({ json: {} });
  });
  await login(page);
  await page.goto('/?view=settings');
  await expect(page.getByLabel('Calendario para las citas')).toHaveValue('shared-calendar');
  await page
    .getByLabel('Calendario para las citas')
    .selectOption({ label: 'Agenda personal · Principal' });
  await expect(page.getByLabel('Calendario para las citas')).toHaveValue('primary-calendar');
  expect(selected).toBe('primary-calendar');
  await expect(page.getByPlaceholder('ID del calendario compartido')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Registro avanzado' })).toHaveCount(0);
  await expect(page.getByRole('columnheader', { name: 'Identificador', exact: true })).toHaveCount(
    0,
  );
  await expect(page.getByRole('link', { name: 'Agregar mi número' })).toBeVisible();
  await page.route('**/api/crm/google/calendars', (route) =>
    route.fulfill({ status: 403, json: { title: 'synthetic provider error' } }),
  );
  await page.reload();
  await expect(
    page.getByText(
      'No pudimos consultar tus calendarios. Vuelve a conectar Google para revisar el acceso.',
    ),
  ).toBeVisible();
  await expect(page.getByRole('button', { name: 'Volver a conectar Google' })).toBeEnabled();
});

test('MR-20 and MR-22 channels say Activo/En pausa; pausing a channel and disconnecting Google ask first', async ({
  page,
}) => {
  await page.route('**/api/crm/settings', (route) =>
    route.request().method() === 'GET'
      ? route.fulfill({
          json: {
            name: 'Hospital sintético',
            guide: '',
            timeZone: 'America/El_Salvador',
            agentEnabled: false,
            googleConnected: true,
            googleCalendarId: 'shared-calendar',
            hospitalConfigured: true,
          },
        })
      : route.fallback(),
  );
  await page.route('**/api/crm/google/calendars', (route) =>
    route.fulfill({
      json: [{ id: 'shared-calendar', name: 'Agenda del hospital', primary: false }],
    }),
  );
  await page.route('**/api/crm/channels', (route) =>
    route.fulfill({
      json: [
        {
          id: 'ch-1',
          name: 'Recepción general',
          phoneNumberId: '111',
          coexistence: false,
          enabled: true,
        },
        {
          id: 'ch-2',
          name: 'Doctor sintético',
          phoneNumberId: '222',
          coexistence: false,
          enabled: false,
        },
      ],
    }),
  );
  const pauses: unknown[] = [];
  await page.route('**/api/crm/channels/ch-1', (route) => {
    if (route.request().method() !== 'PATCH') return route.fallback();
    pauses.push(route.request().postDataJSON());
    return route.fulfill({ json: {} });
  });
  let disconnects = 0;
  await page.route('**/api/crm/google', (route) => {
    if (route.request().method() !== 'DELETE') return route.fallback();
    disconnects++;
    return route.fulfill({ status: 204 });
  });
  await login(page);
  await page.goto('/?view=settings');
  const general = page.getByRole('row').filter({ hasText: 'Recepción general' });
  await expect(general.getByText('Activo', { exact: true })).toBeVisible();
  await expect(
    page
      .getByRole('row')
      .filter({ hasText: 'Doctor sintético' })
      .getByText('En pausa', { exact: true }),
  ).toBeVisible();
  const dialog = page.getByRole('alertdialog');
  await general.getByRole('button', { name: 'Pausar Recepción general' }).click();
  await dialog.getByRole('button', { name: 'Cancelar' }).click();
  expect(pauses).toHaveLength(0);
  await general.getByRole('button', { name: 'Pausar Recepción general' }).click();
  await dialog.getByRole('button', { name: 'Pausar canal' }).click();
  await expect.poll(() => pauses.length).toBe(1);
  expect(pauses[0]).toEqual({ enabled: false });
  await page.getByRole('button', { name: 'Desconectar Google', exact: true }).click();
  await dialog.getByRole('button', { name: 'Cancelar' }).click();
  expect(disconnects).toBe(0);
  await page.getByRole('button', { name: 'Desconectar Google', exact: true }).click();
  await dialog.getByRole('button', { name: 'Desconectar', exact: true }).click();
  await expect.poll(() => disconnects).toBe(1);
});
