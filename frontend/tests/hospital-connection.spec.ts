import { test, expect } from '@playwright/test';
import { login } from './login';
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
test('connection verifies automatically and never asks the business for credentials', async ({
  page,
}) => {
  let finishCheck!: () => void;
  const checkPending = new Promise<void>((resolve) => {
    finishCheck = resolve;
  });
  await page.route('**/api/crm/hospital/connection**', async (route) => {
    const url = new URL(route.request().url());
    if (url.pathname.endsWith('/check')) await checkPending;
    return route.fulfill({
      json: url.pathname.endsWith('/check')
        ? { connected: true }
        : url.pathname.endsWith('/setup')
          ? { allowedOrigins: [], publicUrl: '' }
          : connection,
    });
  });
  await login(page);
  await page.goto('/?view=hospital');
  await expect(page.getByRole('heading', { name: 'Hospital sintético C' })).toBeVisible();

  await expect(page.getByRole('button', { name: 'Comprobar conexión', exact: true })).toHaveCount(
    0,
  );
  await expect(page.getByLabel('Secreto de conexión')).toHaveCount(0);
  await expect(page.getByText('Conectar o actualizar Hospital', { exact: true })).toHaveCount(0);
  await expect(page.getByRole('status')).toHaveText('Conectando con tu hospital…');
  await expect(page.getByRole('link', { name: 'Abrir agenda' })).toHaveCount(0);
  finishCheck();
  await expect(page.getByRole('status')).toHaveText('Hospital conectado');
  await expect(page.getByRole('link', { name: 'Abrir agenda' })).toBeVisible();
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
  await login(page);
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

test('connection outage keeps identity visible and offers a plain-language retry', async ({
  page,
}) => {
  let attempts = 0;
  await page.route('**/api/crm/hospital/connection**', (route) => {
    if (route.request().url().endsWith('/check')) {
      attempts++;
      return attempts === 1
        ? route.fulfill({ status: 503, json: { title: 'hospital.offline' } })
        : route.fulfill({ json: { connected: true } });
    }
    return route.fulfill({ json: connection });
  });
  await login(page);
  await page.goto('/?view=hospital');
  await expect(page.getByRole('status')).toHaveText('Conexión interrumpida');
  await expect(
    page.getByRole('alert').filter({ hasText: 'No pudimos comunicarnos con Hospital' }),
  ).toContainText('Tu cuenta y tus datos siguen vinculados');
  await page.getByRole('button', { name: 'Volver a intentar' }).click();
  await expect(page.getByRole('status')).toHaveText('Hospital conectado');
  await expect(
    page.getByRole('alert').filter({ hasText: 'No pudimos comunicarnos con Hospital' }),
  ).toHaveCount(0);
});
