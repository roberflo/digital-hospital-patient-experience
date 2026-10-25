import { test, expect } from '@playwright/test';
import { login } from './login';
test('business admin gets hosted connection link and imports verified number without IDs', async ({
  page,
}, info) => {
  await login(page);
  let imported = false;
  await page.route('**/api/crm/channels', (route) =>
    route.fulfill({
      json: imported
        ? [
            {
              id: 'channel',
              name: 'WhatsApp · +503 7000 0000',
              phoneNumberId: '1234567890',
              coexistence: true,
            },
          ]
        : [],
    }),
  );
  await page.route('**/api/crm/channels/onboarding', (route) => {
    expect(route.request().postDataJSON()).toEqual({});
    return route.fulfill({
      json: {
        url: 'https://app.kapso.ai/whatsapp/setup/synthetic',
        expiresAt: '2026-11-01T00:00:00Z',
      },
    });
  });
  await page.route('**/api/crm/channels/sync', (route) => {
    expect(route.request().postDataJSON()).toEqual({});
    imported = true;
    return route.fulfill({ json: { connected: 1, added: 1, webhooksReady: 1, warnings: [] } });
  });
  await page.goto('/whatsapp');
  await page.getByRole('button', { name: 'Agregar mi número', exact: true }).click();
  const hosted = page.getByRole('link', { name: 'Continuar conexión en Kapso' });
  await expect(hosted).toHaveAttribute('href', 'https://app.kapso.ai/whatsapp/setup/synthetic');
  await expect(hosted).toHaveAttribute('rel', 'noopener noreferrer');
  await page.getByRole('button', { name: 'Verificar conexión', exact: true }).click();
  await expect(page.getByText('1 número(s) agregado(s) a tu hospital.')).toBeVisible();
  // The history opens the working inbox filtered to that number, not /inbox (unset in production).
  await expect(page.getByRole('link', { name: 'Consultar historial →' })).toHaveAttribute(
    'href',
    '/?view=inbox&channelId=channel',
  );
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({
    path: `../artifacts/whatsapp-connect-${info.project.name}.png`,
    fullPage: true,
  });
});
test('staff cannot start onboarding and malformed provider links are rejected', async ({
  page,
}) => {
  await login(page);
  await page.route('**/api/crm/me', (route) =>
    route.fulfill({ json: { name: 'Agente', role: 'agent' } }),
  );
  await page.goto('/whatsapp');
  await expect(page.getByText('Conexión administrada por tu hospital')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Agregar mi número' })).toHaveCount(0);
});
test('onboarding reports retryable reception setup failure', async ({ page }) => {
  await login(page);
  await page.route('**/api/crm/channels/onboarding', (route) =>
    route.fulfill({ json: { url: 'https://evil.test/setup' } }),
  );
  await page.route('**/api/crm/channels/sync', (route) =>
    route.fulfill({
      json: {
        connected: 1,
        added: 1,
        webhooksReady: 0,
        warnings: ['Número guardado. Reintenta configurar recepción.'],
      },
    }),
  );
  await page.goto('/whatsapp');
  await page.getByRole('button', { name: 'Agregar mi número', exact: true }).click();
  await expect(
    page.getByRole('alert').filter({ hasText: 'El enlace de conexión no es válido.' }),
  ).toBeVisible();
  await expect(page.getByRole('link', { name: 'Continuar conexión en Kapso' })).toHaveCount(0);
  await page.getByRole('button', { name: 'Verificar conexión', exact: true }).click();
  await expect(page.getByText('Número guardado. Reintenta configurar recepción.')).toBeVisible();
});

test('a number history link opens the inbox filtered to that number in every state', async ({
  page,
}) => {
  await login(page);
  await page.route('**/api/crm/channels', (route) =>
    route.fulfill({ json: [{ id: 'channel', name: 'WhatsApp · +503 7000 0000' }] }),
  );
  const asked: URLSearchParams[] = [];
  await page.route('**/api/crm/conversations?*', (route) => {
    asked.push(new URL(route.request().url()).searchParams);
    return route.fulfill({ json: [] });
  });
  await page.goto('/?view=inbox&channelId=channel');
  await expect.poll(() => asked.some((q) => q.get('channelId') === 'channel')).toBe(true);
  const filtered = asked.find((q) => q.get('channelId') === 'channel')!;
  expect(filtered.has('state')).toBe(false);
});
