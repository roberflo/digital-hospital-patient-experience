import { test, expect, type Page } from '@playwright/test';
test.afterEach(async ({ page }) => {
  await page.unrouteAll({ behavior: 'wait' });
});
async function login(page: Page, user = 'admin') {
  await page.goto('/login');
  await page.getByLabel('Usuario de demostración').selectOption(user);
  await page.getByLabel('Contraseña', { exact: true }).fill('demo-recepcion');
  await page.getByRole('button', { name: 'Entrar al espacio' }).click();
  await expect(page.getByRole('heading', { name: 'Dashboard', exact: true })).toBeVisible();
}
async function nav(page: Page, name: string) {
  const menu = page.getByRole('button', { name: 'Abrir menú' });
  if (await menu.isVisible()) await menu.click();
  await page.getByRole('button', { name, exact: true }).click();
}
test('team assigns multiple conversations, filters workload and transfers one with CRM history', async ({
  page,
  browser,
}, info) => {
  // Register the shared synthetic identity through its normal login, not a fake local member.
  const teammate = await browser.newContext();
  const teammatePage = await teammate.newPage();
  await login(teammatePage, 'agent');
  await teammate.close();
  await login(page);
  await nav(page, 'Bandeja de entrada');
  await page.getByLabel('Filtrar por estado', { exact: true }).selectOption('');
  await expect(page.getByRole('link', { name: 'Números ↗', exact: true })).toHaveAttribute(
    'href',
    '/whatsapp',
  );
  for (const name of ['Ana Martínez', 'Carlos Rivera']) {
    await page.getByLabel('Seleccionar conversación de ' + name, { exact: true }).check();
  }
  await page.getByLabel('Responsable de la selección').selectOption('dev-agent');
  await page.getByRole('button', { name: 'Asignar selección', exact: true }).click();
  await expect(page.getByText('Conversaciones asignadas', { exact: true })).toBeVisible();
  await nav(page, 'Equipo');
  const member = page
    .getByRole('row')
    .filter({ has: page.getByText('Recepción demo', { exact: true }) });
  await expect(member).toBeVisible();
  await page.screenshot({ path: `../artifacts/team-${info.project.name}.png`, fullPage: true });
  await member.getByRole('button', { name: 'Ver conversaciones', exact: true }).click();
  await expect(page.getByLabel('Filtrar por responsable')).toHaveValue('member:dev-agent');
  await page.locator('.conversation-card').filter({ hasText: 'Ana Martínez' }).click();
  // Assignment is visible without opening the patient context panel, including on mobile.
  await expect(page.getByLabel('Asignar conversación', { exact: true })).toBeVisible();
  expect((await page.locator('.assignment-bar').boundingBox())!.height).toBeLessThan(100);
  await expect(page.getByLabel('Asignar conversación', { exact: true })).toHaveValue('dev-agent');
  await page.screenshot({
    path: `../artifacts/team-inbox-${info.project.name}.png`,
    fullPage: true,
  });
  await page.getByLabel('Asignar conversación', { exact: true }).selectOption('dev-admin');
  await expect(page.getByText('Responsable actualizado', { exact: true })).toBeVisible();
  // Reassigned work leaves the teammate's queue rather than remaining falsely assigned.
  await expect(page.locator('.conversation-card').filter({ hasText: 'Ana Martínez' })).toHaveCount(
    0,
  );
  await nav(page, 'Actividad');
  await expect(
    page.getByText('Responsable: Recepción demo → Administrador demo.', { exact: true }).first(),
  ).toBeVisible();
  await nav(page, 'Equipo');
  await page.getByRole('button', { name: 'Incorporar compañero', exact: true }).click();
  await expect(
    page.getByRole('heading', { name: 'Incorporar a una persona del hospital' }),
  ).toBeVisible();
  await expect(
    page.getByText('La persona inicia sesión en Recepción con esa cuenta.'),
  ).toBeVisible();
  await page.keyboard.press('Escape');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  // Leave shared demo conversations ready for subsequent tests/users.
  await nav(page, 'Bandeja de entrada');
  await page.getByLabel('Filtrar por responsable').selectOption('all');
  await page.getByLabel('Filtrar por estado').selectOption('');
  for (const name of ['Ana Martínez', 'Carlos Rivera']) {
    await page.getByLabel('Seleccionar conversación de ' + name, { exact: true }).check();
  }
  await page.getByLabel('Responsable de la selección').selectOption('');
  await page.getByRole('button', { name: 'Asignar selección', exact: true }).click();
  await expect(page.getByText('Conversaciones asignadas', { exact: true })).toBeVisible();
});
test('attendant sees team but cannot bulk assign, disable coworkers or edit another owner', async ({
  page,
}) => {
  await login(page, 'agent');
  await nav(page, 'Equipo');
  await expect(
    page.getByRole('heading', { name: 'Equipo de atención', exact: true }),
  ).toBeVisible();
  await expect(page.getByRole('button', { name: 'Desactivar acceso', exact: true })).toHaveCount(0);
  // Read the fixture once: a polling response must not outlive the page's test context.
  const response = await page.request.get('/api/crm/conversations');
  expect(response.ok()).toBe(true);
  const rows = await response.json();
  await page.route('**/api/crm/conversations?*', (route) =>
    route.fulfill({
      json: rows.map((row: { conversation: object }) => ({
        ...row,
        conversation: { ...row.conversation, assignedTo: 'dev-admin' },
      })),
    }),
  );
  await nav(page, 'Bandeja de entrada');
  await page.getByLabel('Filtrar por estado').selectOption('');
  await expect(page.getByLabel('Seleccionar conversaciones visibles')).toHaveCount(0);
  await page.locator('.conversation-card').filter({ hasText: 'Ana Martínez' }).click();
  await expect(page.getByLabel('Asignar conversación', { exact: true })).toBeDisabled();
  await expect(page.getByText('El responsable o un supervisor puede transferirla.')).toBeVisible();
});
