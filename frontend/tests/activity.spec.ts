import { test, expect, type Page } from '@playwright/test';
import { login as signIn } from './login';
const contact = '11111111-aaaa-4111-8111-111111111111';
const conversation = '11111111-bbbb-4111-8111-111111111111';
const longNote = 'Seguimiento sintético. '.repeat(20) + 'Fin de la nota clínica interna.';
const events = [
  {
    id: 'ai',
    actor: 'Agente',
    actorRole: 'agent_ai',
    careType: 'ai',
    kind: 'response',
    category: 'response',
    body: 'Orientó al paciente sobre horarios.',
    deliveryStatus: 'read',
  },
  {
    id: 'human',
    actor: 'Rosa Recepción',
    actorRole: 'agent',
    careType: 'reception',
    kind: 'note',
    category: 'note',
    body: longNote,
  },
  {
    id: 'doctor',
    actor: 'Dra. Elena',
    actorRole: 'doctor',
    careType: 'doctor',
    kind: 'clinical_review',
    category: 'clinical',
    body: 'Consultó Hospital durante la atención.',
  },
  {
    id: 'legacy',
    actor: 'Equipo anterior',
    actorRole: 'unknown',
    careType: 'unknown',
    kind: 'handoff',
    category: 'handoff',
    body: 'Atención humana solicitada.',
  },
].map((event) => ({
  ...event,
  createdAt: '2026-10-03T16:00:00Z',
  contactId: contact,
  patientName: 'María Paciente sintética',
  conversationId: conversation,
  conversationState: 'pending',
  channelName: 'Recepción general',
}));
async function login(page: Page) {
  await signIn(page);
  await page.goto('/?view=activity');
}
test.afterEach(async ({ page }) => {
  await page.unrouteAll({ behavior: 'wait' });
});
test('care history identifies AI, reception and doctors, searches, filters and opens context', async ({
  page,
}, info) => {
  const queries: URLSearchParams[] = [];
  await page.route('**/api/crm/activity-feed?*', (route) => {
    const params = new URL(route.request().url()).searchParams;
    queries.push(params);
    const rows = events.filter(
      (event) =>
        (!params.get('care') || event.careType === params.get('care')) &&
        (!params.get('q') ||
          (event.actor + event.body).toLowerCase().includes(params.get('q')!.toLowerCase())),
    );
    return route.fulfill({
      json: {
        items: rows,
        total: rows.length,
        page: 1,
        pageSize: 30,
        careCounts: { ai: 1, reception: 1, doctor: 1, unknown: 1 },
      },
    });
  });
  await login(page);
  await expect(page.getByText('4 acciones encontradas')).toBeVisible();
  await expect(page.locator('.care-role').filter({ hasText: 'Agente IA' })).toBeVisible();
  await expect(page.locator('.care-role').filter({ hasText: 'Recepción humana' })).toBeVisible();
  await expect(page.locator('.care-role').filter({ hasText: 'Doctores' })).toBeVisible();
  await expect(
    page.getByText('Este registro anterior no guardó el rol de quien realizó la acción.'),
  ).toBeVisible();
  await expect(page.getByText('Leído', { exact: true })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Abrir conversación' }).first()).toHaveAttribute(
    'href',
    '/?view=inbox&conversationId=' + conversation,
  );
  await page.getByRole('button', { name: 'Leer nota completa' }).click();
  await expect(page.getByText(longNote, { exact: true })).toBeVisible();
  await page.screenshot({ path: `../artifacts/activity-${info.project.name}.png`, fullPage: true });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.getByLabel('Quién realizó la acción').selectOption('doctor');
  await expect(page.getByText('1 acción encontrada')).toBeVisible();
  await expect(page.getByText('Dra. Elena', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Limpiar filtros' }).click();
  await page.getByLabel('Buscar en el historial').fill('Rosa');
  await expect(page.getByText('1 acción encontrada')).toBeVisible();
  await expect(page.getByText('Rosa Recepción', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Ver historial de María Paciente sintética' }).click();
  await expect.poll(() => queries.at(-1)?.get('contactId')).toBe(contact);
  await page.getByLabel('Buscar en el historial').fill('Sin coincidencias');
  await expect(page.getByText('No hay actividad que coincida')).toBeVisible();
  await page.getByRole('button', { name: 'Mostrar toda la actividad' }).click();
  await page.getByLabel('Desde', { exact: true }).fill('2026-10-04');
  await page.getByLabel('Hasta', { exact: true }).fill('2026-10-03');
  await expect(page.getByRole('alert').filter({ hasText: 'La fecha Desde' })).toBeVisible();
});
test('history paginates and resets to first page when filters change', async ({ page }) => {
  await page.route('**/api/crm/activity-feed?*', (route) => {
    const params = new URL(route.request().url()).searchParams;
    return route.fulfill({
      json: {
        items: [events[Number(params.get('page')) === 2 ? 2 : 0]],
        total: 31,
        page: Number(params.get('page')),
        pageSize: 30,
        careCounts: { ai: 30, doctor: 1 },
      },
    });
  });
  await login(page);
  await page.getByRole('button', { name: 'Siguiente', exact: true }).click();
  await expect(page.getByText('Página 2 de 2')).toBeVisible();
  await expect(page.getByText('Dra. Elena', { exact: true })).toBeVisible();
  await page.getByLabel('Quién realizó la acción').selectOption('ai');
  await expect(page.getByText('Página 1 de 2')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Anterior', exact: true })).toBeDisabled();
});
test('activity conversation link opens its CRM thread and can return to the full queue', async ({
  page,
  isMobile,
}) => {
  await login(page);
  const chats = await (await page.request.get('/api/crm/conversations')).json();
  const target = chats.find(
    (row: { contact: { name: string } }) => row.contact.name === 'Ana Martínez',
  );
  expect(target).toBeTruthy();
  await page.goto('/?view=inbox&conversationId=' + target.conversation.id);
  await expect(page.getByLabel('Mensaje al paciente')).toBeVisible();
  await expect(page.locator('.conversation-card')).toHaveCount(1);
  if (isMobile)
    await page.getByRole('button', { name: 'Volver a conversaciones', exact: true }).click();
  else await page.getByRole('button', { name: 'Ver todas', exact: true }).click();
  await expect.poll(() => page.locator('.conversation-card').count()).toBeGreaterThan(1);
  expect(new URL(page.url()).searchParams.has('conversationId')).toBe(false);
});
