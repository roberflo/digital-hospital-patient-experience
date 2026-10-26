import { test, expect, type Page, type Route } from '@playwright/test';
import { enter } from './login';

// docs/usage-report.md AC 15–16: «Uso del mes» in the platform owner's view, with the API mocked.
const counts = (n: number) => ({
  numbersConnected: n,
  numbersEnabled: n,
  patientMessages: 1200 * n,
  agentReplies: 900 * n,
  humanReplies: 40 * n,
  templatesSent: 30 * n,
  agentConversations: 150 * n,
  appointments: 20 * n,
  registrations: 5 * n,
  prescriptions: 3 * n,
  aiCalls: null,
  inputTokens: null,
  outputTokens: null,
  cachedInputTokens: null,
});
const report = (month: string) => ({
  month,
  aiMetered: false,
  tenants: [
    {
      id: '72000000-0000-4000-8000-000000000001',
      name: 'Clínica Sintética Norte',
      usage: counts(1),
    },
    { id: '72000000-0000-4000-8000-000000000002', name: 'Clínica Sintética Sur', usage: counts(2) },
  ],
  totals: counts(3),
});
async function enterAsOwner(page: Page, usage: (route: Route, month: string) => unknown) {
  await page.route('**/api/crm/platform/usage?*', (route) =>
    usage(route, new URL(route.request().url()).searchParams.get('month') ?? ''),
  );
  await page.context().clearCookies();
  await page.goto('/login');
  await enter(page, 'platform');
  await expect(page.getByRole('heading', { name: 'Recepciones', level: 1 })).toBeVisible();
  return page.getByRole('region', { name: 'Uso del mes' });
}

test('the owner reads each clinic and the totals for the month, AI as «sin medir»', async ({
  page,
}) => {
  const asked: string[] = [];
  const section = await enterAsOwner(page, (route, month) => {
    asked.push(month);
    return route.fulfill({ json: report(month) });
  });
  const now = new Date();
  const current = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}`;
  await expect(section.getByLabel('Mes')).toHaveValue(current);
  const table = section.getByRole('table');
  const north = table.getByRole('row', { name: /Clínica Sintética Norte/ });
  await expect(north.getByRole('cell').nth(2)).toHaveText(/^1[.,\s ]?200$/);
  const total = table.getByRole('row', { name: /Total/ });
  await expect(total.getByRole('cell').nth(2)).toHaveText(/^3[.,\s ]?600$/);
  await expect(total.getByRole('cell').nth(7)).toHaveText('60');
  await expect(north.getByRole('cell').nth(10)).toHaveText('sin medir');
  // Numbers line up: right-aligned, same-width digits.
  const style = await north
    .getByRole('cell')
    .nth(2)
    .evaluate((cell) => [
      getComputedStyle(cell).textAlign,
      getComputedStyle(cell).fontVariantNumeric,
    ]);
  expect(style).toEqual(['right', 'tabular-nums']);
  expect(asked).toEqual([current]);

  await section.getByLabel('Mes').fill('2026-09');
  await expect(table).toHaveAccessibleName('Uso por clínica, 2026-09');
  expect(asked).toContain('2026-09');
  // 360 px: the table scrolls inside its card; the page itself never scrolls sideways.
  await page.setViewportSize({ width: 360, height: 800 });
  await expect(north).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
});

test('a failed read says so and retries; an installation without clinics says it', async ({
  page,
}) => {
  let fail = true;
  let empty = false;
  const section = await enterAsOwner(page, (route, month) =>
    fail
      ? route.fulfill({ status: 500, json: { title: 'falló' } })
      : route.fulfill({ json: empty ? { ...report(month), tenants: [] } : report(month) }),
  );
  await expect(section.getByText('La lectura falló: no significa cero.')).toBeVisible();
  await expect(section.getByRole('table')).toHaveCount(0);
  fail = false;
  await section.getByRole('button', { name: 'Reintentar' }).click();
  await expect(section.getByRole('row', { name: /Clínica Sintética Sur/ })).toBeVisible();

  empty = true;
  await section.getByLabel('Mes').fill('2026-01');
  await expect(section.getByText('Aún no hay clínicas')).toBeVisible();
});
