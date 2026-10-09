import { test, expect, type Page } from '@playwright/test';
import { devPassword, login, openMenu, users, type User } from './login';

const subject = async (page: Page): Promise<string> =>
  (await (await page.request.get('/api/crm/me')).json()).subject;
const keycloak = (page: Page) => page.locator('#username');
async function credentials(page: Page, user: User) {
  // prompt=login re-asks the SSO user's password with the username locked; "Restart login" frees it.
  if (await page.getByRole('button', { name: 'Restart login' }).isVisible())
    await page.getByRole('button', { name: 'Restart login' }).click();
  await keycloak(page).fill(users[user].username);
  await page.locator('#password').fill(devPassword());
  await page.locator('#kc-login').click();
}
/** Subjects of two people, then a Recepción + Keycloak session for `admin`. */
async function twoPeople(page: Page) {
  await login(page, 'other');
  const other = await subject(page);
  await login(page, 'admin');
  return { admin: await subject(page), other };
}

test('the same person enters Recepción without seeing Keycloak', async ({ page }) => {
  const { admin } = await twoPeople(page);
  await page.goto('/login?as=' + admin);
  await page.waitForURL((url) => url.pathname === '/');
  await openMenu(page);
  await expect(page.locator('.profile')).toContainText(users.admin.name);
});

test('another person is asked for credentials and enters as them', async ({ page }) => {
  const { other } = await twoPeople(page);
  await page.goto('/login?as=' + other);
  await expect(page.locator('#password')).toBeVisible();
  await credentials(page, 'other');
  await page.waitForURL((url) => url.pathname === '/');
  await openMenu(page);
  await expect(page.locator('.profile')).toContainText(users.other.name);
});

test('an invalid subject shows the ordinary login page', async ({ page }) => {
  await page.context().clearCookies();
  await page.goto('/login?as=not-a-uuid');
  await expect(
    page.getByRole('button', { name: 'Continuar con mi cuenta del hospital' }),
  ).toBeVisible();
  await page.waitForTimeout(1500);
  expect(new URL(page.url()).pathname).toBe('/login');
});

test('a wrong account twice stops with an alert instead of looping', async ({ page }) => {
  const { other } = await twoPeople(page);
  await page.goto('/login?as=' + other);
  // Keycloak re-asks the SSO user (admin) for the password: the wrong account comes back again.
  await page.locator('#password').fill(devPassword());
  await page.locator('#kc-login').click();
  await expect(
    page.getByRole('alert').filter({ hasText: 'Entraste con una cuenta distinta' }),
  ).toBeVisible();
  expect(page.url()).toContain('step=2');
  await expect(
    page.getByRole('button', { name: 'Continuar con mi cuenta del hospital' }),
  ).toBeVisible();
});
