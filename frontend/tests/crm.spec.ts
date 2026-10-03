import { test, expect } from '@playwright/test';
async function login(page: import('@playwright/test').Page) {
  await page.goto('/login');
  await page.getByLabel('Contraseña', { exact: true }).fill('demo-recepcion');
  await page.getByRole('button', { name: 'Entrar al espacio' }).click();
  await expect(page.getByRole('heading', { name: 'Bandeja de entrada' })).toBeVisible();
}
async function nav(page: import('@playwright/test').Page, name: string) {
  const menu = page.getByRole('button', { name: 'Abrir menú' });
  if (await menu.isVisible()) await menu.click();
  await page.getByRole('button', { name, exact: true }).click();
}
test('login, inbox, conversation and internal note persist', async ({ page }, info) => {
  await login(page);
  await page
    .getByRole('button')
    .filter({ has: page.getByText('Ana Martínez', { exact: true }) })
    .click();
  await expect(
    page.getByRole('heading', { name: 'Ana Martínez', exact: true }).first(),
  ).toBeVisible();
  await page.getByRole('button', { name: 'Nota interna', exact: true }).click();
  const note = 'Nota sintética E2E ' + Date.now();
  await page.getByLabel('Nota de seguimiento').fill(note);
  await page.getByRole('button', { name: 'Guardar', exact: true }).click();
  await expect(page.getByText('Cambios guardados', { exact: true })).toBeVisible();
  await nav(page, 'Actividad');
  await expect(page.getByText(note, { exact: true })).toBeVisible();
  await nav(page, 'Bandeja de entrada');
  await page.screenshot({ path: `../artifacts/inbox-${info.project.name}.png`, fullPage: true });
  expect(
    await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
  ).toBeTruthy();
});
test('contact creation and pipeline work through authenticated BFF', async ({ page }, info) => {
  await login(page);
  await nav(page, 'Contactos');
  await page.getByRole('button', { name: 'Nuevo contacto', exact: true }).click();
  const name = 'Paciente E2E ' + info.project.name + ' ' + Date.now();
  await page.getByLabel('Nombre completo').fill(name);
  await page
    .getByLabel('Teléfono con código de país')
    .fill('503' + Date.now().toString().slice(-8));
  await page.getByRole('button', { name: 'Guardar', exact: true }).click();
  await expect(page.getByText(name, { exact: true })).toBeVisible();
  await nav(page, 'Oportunidades');
  await page.getByRole('button', { name: 'Nuevo seguimiento', exact: true }).click();
  await page.getByLabel('Título', { exact: true }).fill('Seguimiento ' + name);
  await page.getByLabel('Contacto', { exact: true }).selectOption({ label: name });
  await page.getByRole('button', { name: 'Guardar', exact: true }).click();
  await expect(
    page.getByRole('heading', { name: 'Seguimiento ' + name, exact: true }),
  ).toBeVisible();
  await page.getByLabel('Etapa de Seguimiento ' + name).selectOption('scheduled');
  await expect(
    page
      .locator('.kanban-column')
      .filter({ has: page.getByRole('heading', { name: 'Agendado', exact: true }) }),
  ).toContainText('Seguimiento ' + name);
});
test('settings and connection state are honest; mobile layout fits', async ({ page }, info) => {
  await login(page);
  await nav(page, 'Configuración');
  await expect(
    page.getByRole('heading', { name: 'Tu hospital y su guía de atención' }),
  ).toBeVisible();
  await expect(page.getByText('Envío en pausa', { exact: true })).toBeVisible();
  await page.screenshot({ path: `../artifacts/settings-${info.project.name}.png`, fullPage: true });
  expect(
    await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
  ).toBeTruthy();
});
test('BFF rejects unauthenticated access', async ({ request }) => {
  const response = await request.get('/api/crm/contacts');
  expect(response.status()).toBe(401);
});
