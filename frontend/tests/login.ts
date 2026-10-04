import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { expect, type Page } from '@playwright/test';

// Hospital's Keycloak is the only way in. These are its synthetic dev users (Hospital's
// infra/keycloak/configure-realms.sh, KC_SEED_DEV_USERS=true); tenants A and B hold demo data.
export const users = {
  admin: { username: 'dev-administrador-a', name: 'Alba Sintética Aguilar' },
  agent: { username: 'dev-recepcion-a', name: 'Rosa Sintética Aguilar' },
  doctor: { username: 'dev-medicos-a', name: 'Dra. Mónica Sintética Aguilar' },
  other: { username: 'dev-administrador-b', name: 'Alba Sintética Bonilla' },
  hospital: { username: 'dev-recepcion-c', name: 'Rosa Sintética Castillo' },
} as const;
export type User = keyof typeof users;

export function devPassword() {
  if (process.env.KC_DEV_USERS_PASSWORD) return process.env.KC_DEV_USERS_PASSWORD;
  const file = process.env.HOSPITAL_ENV ?? resolve(__dirname, '../../../Hospital/.env');
  const line = readFileSync(file, 'utf8')
    .split('\n')
    .find((x) => x.startsWith('KC_DEV_USERS_PASSWORD='));
  if (!line) throw new Error('KC_DEV_USERS_PASSWORD not found: start Hospital with dev users');
  return line.slice(line.indexOf('=') + 1).trim();
}

/** From Recepción's login page, through Keycloak's own page and back. Needs no live SSO session. */
export async function enter(page: Page, user: User = 'admin') {
  await page.getByRole('button', { name: 'Continuar con mi cuenta del hospital' }).click();
  await page.locator('#username').fill(users[user].username);
  await page.locator('#password').fill(devPassword());
  await page.locator('#kc-login').click();
  // Back on Recepción: off Keycloak (/realms), past the callback (/api/auth) and the login page.
  await page.waitForURL((url) => !/^\/(realms|api\/auth|login)/.test(url.pathname));
}

/** Signs in through Keycloak's own page. Other users' SSO cookies are dropped first. */
export async function login(page: Page, user: User = 'admin', returnTo = '/') {
  await page.context().clearCookies();
  await page.goto(
    '/login' + (returnTo === '/' ? '' : '?callbackUrl=' + encodeURIComponent(returnTo)),
  );
  await enter(page, user);
  if (returnTo === '/')
    await expect(page.getByRole('heading', { name: 'Dashboard', exact: true })).toBeVisible();
}
