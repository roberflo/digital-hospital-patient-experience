import { test, expect, type Page, type Request } from '@playwright/test';
import { login } from './login';
// MR-45 (docs/mejoras-revision-2026-10.md): a doctor with their own number enters the inbox filtered
// to it and can clear the filter; a link to a conversation, phone or channel wins over that default.
test.afterEach(async ({ page }) => {
  await page.unrouteAll({ behavior: 'wait' });
});
const doctor = 'sub-doctor-sintetico';
const channels = [
  {
    id: 'ch-general',
    name: 'WhatsApp · recepción general',
    phoneNumberId: '1',
    coexistence: false,
    enabled: true,
  },
  {
    id: 'ch-other',
    name: 'WhatsApp · Dr. Otro',
    phoneNumberId: '2',
    doctorId: 'sub-otro',
    coexistence: false,
    enabled: true,
  },
  {
    id: 'ch-doc',
    name: 'WhatsApp · Dra. Sintética',
    phoneNumberId: '3',
    doctorId: doctor,
    coexistence: false,
    enabled: true,
  },
];
const isList = (r: Request) => new URL(r.url()).pathname === '/api/crm/conversations';
const channelOf = (r: Request) => new URL(r.url()).searchParams.get('channelId');

async function asDoctor(page: Page) {
  await login(page);
  // /api/me answers the role as Recepción maps it ("doctor") and the token's `sub` as subject.
  await page.route('**/api/crm/me', async (route) => {
    const response = await route.fetch();
    await route.fulfill({
      response,
      json: { ...(await response.json()), role: 'doctor', subject: doctor },
    });
  });
  await page.route('**/api/crm/channels', (route) => route.fulfill({ json: channels }));
  await page.route('**/api/crm/conversations?*', (route) => route.fulfill({ json: [] }));
  const seen: Request[] = [];
  page.on('request', (r) => {
    if (isList(r)) seen.push(r);
  });
  return seen;
}

test('a doctor enters filtered to their own number and can clear the filter', async ({ page }) => {
  const seen = await asDoctor(page);
  const filtered = page.waitForRequest((r) => isList(r) && channelOf(r) === 'ch-doc');
  await page.goto('/?view=inbox');
  await filtered;
  const select = page.getByLabel('Filtrar por canal');
  await expect(select).toHaveValue('ch-doc');
  const cleared = page.waitForRequest((r) => isList(r) && channelOf(r) === null);
  await select.selectOption('');
  await cleared;
  // The default is applied once: the next poll still reads every number of the hospital.
  const from = seen.length;
  await page.waitForRequest(isList);
  expect(seen.slice(from).map(channelOf)).toEqual(seen.slice(from).map(() => null));
  await expect(select).toHaveValue('');
});

test('a link to a conversation is not overridden by the doctor filter', async ({ page }) => {
  const seen = await asDoctor(page);
  const loaded = page.waitForResponse('**/api/crm/channels');
  await page.goto('/?view=inbox&conversationId=00000000-0000-4000-8000-000000000045');
  await loaded;
  // The first render's read can still land after the channels; the second (key change or the 5 s
  // poll) follows the render that saw them: had the default applied, it would carry ch-doc.
  const from = seen.length;
  await expect.poll(() => seen.length, { timeout: 15000 }).toBeGreaterThanOrEqual(from + 2);
  expect(new URL(seen.at(-1)!.url()).searchParams.get('conversationId')).toBe(
    '00000000-0000-4000-8000-000000000045',
  );
  expect(seen.map(channelOf)).toEqual(seen.map(() => null));
});
