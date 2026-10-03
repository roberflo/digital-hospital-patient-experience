import { test, expect } from '@playwright/test';
const contactId = '71000000-0000-4000-8000-000000000001';
const patientId = '71000000-0000-4000-8000-000000000002';
const connection = {
  hospital: {
    id: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
    name: 'Hospital sintético C',
    timeZone: 'America/El_Salvador',
  },
  configured: true,
  sharedIdentity: true,
  hospitalLoginAvailable: true,
  name: 'Recepción sintética',
  role: 'admin',
};
test('connection shows hospital identity and verifies before claiming connected', async ({
  page,
}) => {
  await page.route('**/api/crm/hospital/connection**', async (route) => {
    const url = new URL(route.request().url());
    return route.fulfill({
      json: url.pathname.endsWith('/check')
        ? { connected: true }
        : url.pathname.endsWith('/setup')
          ? { allowedOrigins: [], publicUrl: '' }
          : connection,
    });
  });
  await page.goto('/login');
  await page.getByLabel('Contraseña', { exact: true }).fill('demo-recepcion');
  await page.getByRole('button', { name: 'Entrar al espacio' }).click();
  await page.waitForURL('/');
  await page.goto('/?view=hospital');
  await expect(page.getByRole('heading', { name: 'Hospital sintético C' })).toBeVisible();
  await expect(page.getByText('Conexión comprobada con la agenda de este hospital.')).toHaveCount(
    0,
  );
  await page.getByRole('button', { name: 'Comprobar conexión', exact: true }).click();
  await expect(page.getByText('Conexión comprobada con la agenda de este hospital.')).toBeVisible();
  expect(
    await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),
  ).toBeTruthy();
});
test('patient search requires explicit selection and phone verification when linking', async ({
  page,
}) => {
  let linked = false;
  let searches = 0;
  await page.route('**/api/crm/hospital/connection', (route) =>
    route.fulfill({ json: connection }),
  );
  await page.route('**/api/crm/contacts?**', (route) =>
    route.fulfill({
      json: [
        {
          id: contactId,
          name: 'Contacto sintético',
          phone: '50370000001',
          email: '',
          tags: '',
          lifecycleStage: 'lead',
        },
      ],
    }),
  );
  await page.route(`**/api/crm/hospital/contacts/${contactId}/patients/search`, (route) => {
    expect(route.request().postDataJSON()).toEqual({
      queryShape: 'name-tokens',
      term: 'Paciente sintético',
    });
    searches++;
    return route.fulfill({
      json: { results: [{ patientId, displayName: 'Paciente sintético', recordNumber: 'C-001' }] },
    });
  });
  await page.route(`**/api/crm/contacts/${contactId}/patient`, (route) => {
    expect(route.request().postDataJSON()).toEqual({ patientId });
    linked = true;
    return route.fulfill({ status: 404, json: { title: 'hospital.patient_phone_mismatch' } });
  });
  await page.goto('/login');
  await page.getByLabel('Contraseña', { exact: true }).fill('demo-recepcion');
  await page.getByRole('button', { name: 'Entrar al espacio' }).click();
  await page.waitForURL('/');
  await page.goto('/?view=contacts');
  await page.getByRole('button', { name: 'Vincular paciente', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel('Datos del paciente').fill('Paciente sintético');
  await dialog.getByRole('button', { name: 'Buscar paciente', exact: true }).click();
  await expect(dialog.getByText('Expediente: C-001')).toBeVisible();
  expect(searches).toBe(1);
  expect(linked).toBe(false);
  await dialog.getByRole('radio').check();
  expect(linked).toBe(false);
  await dialog.getByRole('button', { name: 'Confirmar vínculo con Paciente sintético' }).click();
  await expect(dialog.getByRole('alert')).toHaveText(
    'El teléfono del expediente no coincide con el contacto. Corrige los datos en Hospital antes de vincular.',
  );
  expect(linked).toBe(true);
  expect(
    await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),
  ).toBeTruthy();
});
