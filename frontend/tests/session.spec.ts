import { test, expect, type Page } from '@playwright/test';
import { enter, login, users } from './login';
test.afterEach(async ({ page }) => {
  await page.unrouteAll({ behavior: 'wait' });
});
async function draftConversation(page: Page) {
  await login(page);
  await page.goto('/?view=inbox');
  await page.getByLabel('Filtrar por estado').selectOption('');
  await page.locator('.conversation-card').filter({ hasText: 'Ana Martínez' }).click();
  await page.getByLabel('Mensaje al paciente').fill('Borrador sintético que se conserva');
}
test('expired session preserves draft and selection, renews in another tab and never replays a send', async ({
  page,
  context,
}, info) => {
  await draftConversation(page);
  let sends = 0;
  page.on('request', (req) => {
    if (req.method() === 'POST' && /\/api\/crm\/conversations\/[^/]+\/messages$/.test(req.url()))
      sends++;
  });
  await page.route('**/api/crm/conversations/*/messages', async (route) => {
    if (route.request().method() !== 'POST') return route.continue();
    await context.clearCookies();
    await route.fulfill({ status: 401, json: { title: 'Sesión expirada' } });
  });
  await page.getByRole('button', { name: 'Enviar', exact: true }).click();
  const dialog = page.getByRole('dialog').filter({ hasText: 'Tu sesión terminó' });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Cerrar', exact: true })).toHaveCount(0);
  await page.keyboard.press('Escape');
  await expect(dialog).toBeVisible();
  await page.screenshot({
    path: `../artifacts/session-expired-${info.project.name}.png`,
    fullPage: true,
  });
  const newTab = context.waitForEvent('page');
  await dialog.getByRole('link', { name: 'Iniciar sesión y continuar' }).click();
  const auth = await newTab;
  await expect(auth.getByRole('heading', { name: 'Recupera tu sesión' })).toBeVisible();
  await enter(auth);
  await expect(auth.getByRole('heading', { name: 'Ya puedes continuar' })).toBeVisible();
  await expect(dialog).not.toBeVisible();
  await expect(page.getByLabel('Mensaje al paciente')).toHaveValue(
    'Borrador sintético que se conserva',
  );
  await expect(
    page.getByRole('heading', { name: 'Ana Martínez', exact: true }).first(),
  ).toBeVisible();
  await expect(page).toHaveURL(/\?view=inbox$/);
  expect(sends).toBe(1);
  await auth.close();
});
test('login returns to requested workspace and rejects external callback', async ({ page }) => {
  await page.goto('/?view=calendar');
  await expect(page).toHaveURL(/\/login\?callbackUrl=/);
  await enter(page);
  await expect(page).toHaveURL('/?view=calendar');
  await expect(page.getByRole('heading', { name: 'Agenda', exact: true })).toBeVisible();
  await login(page, 'admin', 'https://evil.example');
  expect(page.url()).not.toContain('evil.example');
  await expect(page.getByRole('heading', { name: 'Dashboard', exact: true })).toBeVisible();
});
test('temporary server failure is not presented as expired login', async ({ page }) => {
  await draftConversation(page);
  await page.route('**/api/crm/conversations/*/messages', (route) =>
    route.request().method() === 'POST'
      ? route.fulfill({ status: 503, json: { title: 'Conexión temporalmente interrumpida' } })
      : route.continue(),
  );
  await page.getByRole('button', { name: 'Enviar', exact: true }).click();
  await expect(
    page.getByText('Conexión temporalmente interrumpida', { exact: true }),
  ).toBeVisible();
  await expect(page.getByRole('dialog').filter({ hasText: 'Tu sesión terminó' })).toHaveCount(0);
  await expect(page.getByLabel('Mensaje al paciente')).toHaveValue(
    'Borrador sintético que se conserva',
  );
});
test('recovery with another account clears old workspace instead of reusing its drafts', async ({
  page,
  context,
}) => {
  await draftConversation(page);
  await page.route('**/api/crm/conversations/*/messages', async (route) => {
    if (route.request().method() !== 'POST') return route.continue();
    await context.clearCookies();
    await route.fulfill({ status: 401, json: { title: 'Sesión expirada' } });
  });
  await page.getByRole('button', { name: 'Enviar', exact: true }).click();
  const dialog = page.getByRole('dialog').filter({ hasText: 'Tu sesión terminó' });
  await expect(dialog).toBeVisible();
  const newTab = context.waitForEvent('page');
  await dialog.getByRole('link', { name: 'Iniciar sesión y continuar' }).click();
  const auth = await newTab;
  await enter(auth, 'doctor');
  await expect(auth.getByRole('heading', { name: 'Ya puedes continuar' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Dashboard', exact: true })).toBeVisible();
  await expect(page.locator('.profile')).toContainText(users.doctor.name);
  await expect(page.getByLabel('Mensaje al paciente')).toHaveCount(0);
  await auth.close();
});
