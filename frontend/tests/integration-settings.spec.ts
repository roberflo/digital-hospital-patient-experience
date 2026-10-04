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
