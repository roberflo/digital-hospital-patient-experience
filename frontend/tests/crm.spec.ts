import { test, expect } from '@playwright/test';
import { login, openMenu } from './login';
// A handler still running route.fetch() for the inbox's polling when its test ends fails the next
// test in the worker with "Test ended": let every handler finish first, as the other specs do.
test.afterEach(async ({ page }) => {
  await page.unrouteAll({ behavior: 'wait' });
});
async function nav(page: import('@playwright/test').Page, name: string) {
  await openMenu(page);
  await page.getByRole('button', { name, exact: true }).click();
}
test('dashboard and inbox are separate; conversation and internal note persist', async ({
  page,
}, info) => {
  // Keep the unread rendering case deterministic even after earlier local runs marked Ana read.
  await page.route('**/api/crm/conversations?*', async (route) => {
    const response = await route.fetch();
    const chats = await response.json();
    await route.fulfill({
      response,
      json: chats.map((chat: { contact: { name: string }; unreadCount: number }) =>
        chat.contact.name === 'Ana Martínez' ? { ...chat, unreadCount: 1 } : chat,
      ),
    });
  });
  await login(page);
  await expect(page.getByText('Conversaciones abiertas', { exact: true })).toBeVisible();
  await page.screenshot({
    path: `../artifacts/dashboard-${info.project.name}.png`,
    fullPage: true,
  });
  await page.getByRole('button', { name: 'Abrir bandeja', exact: true }).click();
  await expect(page).toHaveURL(/view=inbox/);
  await expect(page.getByText('Conversaciones abiertas', { exact: true })).toHaveCount(0);
  await expect(page.getByText('ATENCIÓN CONECTADA', { exact: true })).toHaveCount(0);
  const inbox = await page.locator('.inbox-layout').boundingBox();
  expect(inbox!.y).toBeLessThan(180);
  expect(inbox!.y + inbox!.height).toBeLessThanOrEqual(page.viewportSize()!.height);
  await page.reload();
  await expect(
    page.getByRole('heading', { name: 'Bandeja de entrada', exact: true }),
  ).toBeVisible();
  await expect(page.getByLabel('1 mensajes sin leer').first()).toBeVisible();
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
  const settings = await (await page.request.get('/api/crm/settings')).json();
  const sendLabel = settings.sendEnabled
    ? 'Envío habilitado'
    : settings.manualSendEnabled
      ? 'Envío manual habilitado'
      : 'Envío en pausa';
  await expect(page.getByText(sendLabel, { exact: true })).toBeVisible();
  await page.screenshot({ path: `../artifacts/settings-${info.project.name}.png`, fullPage: true });
  expect(
    await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
  ).toBeTruthy();
});
test('BFF rejects unauthenticated access', async ({ request }) => {
  const response = await request.get('/api/crm/contacts');
  expect(response.status()).toBe(401);
});

test('inbox updates conversation workflow and shared CRM profile', async ({ page }, info) => {
  await login(page);
  await nav(page, 'Bandeja de entrada');
  await page.getByLabel('Filtrar por estado').selectOption('');
  await page
    .getByRole('button')
    .filter({ has: page.getByText('Ana Martínez', { exact: true }) })
    .click();
  if (!(await page.getByLabel('Estado de conversación', { exact: true }).isVisible())) {
    await page.getByRole('button', { name: 'Ver información del paciente' }).click();
  }
  await page.getByLabel('Estado de conversación', { exact: true }).selectOption('pending');
  await expect(page.getByLabel('Estado de conversación', { exact: true })).toHaveValue('pending');
  await page.getByLabel('Prioridad', { exact: true }).selectOption('high');
  await expect(page.getByLabel('Prioridad', { exact: true })).toHaveValue('high');
  await page.getByRole('button', { name: 'Editar ficha', exact: true }).click();
  const email = 'inbox-' + info.project.name + '@example.invalid';
  await page.getByLabel('Correo del cliente').fill(email);
  await expect(page.getByLabel('Estado del cliente').locator('option[value=active]')).toHaveCount(
    0,
  );
  await page.getByLabel('Estado del cliente').selectOption('inactive');
  await page.getByRole('button', { name: 'Guardar ficha CRM', exact: true }).click();
  await expect(page.getByText('Ficha CRM actualizada', { exact: true })).toBeVisible();
  await expect(page.getByText('Contacto · Inactivo', { exact: true })).toBeVisible();
  await page.getByLabel('Estado de conversación', { exact: true }).selectOption('open');
  await expect(page.getByLabel('Estado de conversación', { exact: true })).toHaveValue('open');
  await page.getByLabel('Prioridad', { exact: true }).selectOption('normal');
  await expect(page.getByLabel('Prioridad', { exact: true })).toHaveValue('normal');
  // On mobile the patient context is a sheet: it is open here, even while a dialog inside it closes.
  if (await page.locator('[data-slot=sheet-content]').count()) {
    await page.getByRole('button', { name: 'Cerrar detalles' }).click();
  }
  await nav(page, 'Contactos');
  // The demo tenant keeps more than 100 contacts after a few runs: find Ana, not the first page.
  await page.getByLabel('Buscar', { exact: true }).fill('Ana Martínez');
  await expect(page.getByText(email, { exact: true })).toBeVisible();
});

test('personal views persist and conversation search filters messages', async ({ page }, info) => {
  await login(page);
  await nav(page, 'Bandeja de entrada');
  await page.getByLabel('Filtrar por estado').selectOption('pending');
  await page.getByRole('button', { name: 'Administrar vistas guardadas' }).click();
  const view = 'Pendientes E2E ' + info.project.name + Date.now();
  await page.getByLabel('Nombre de la vista').fill(view);
  await page.getByRole('button', { name: 'Guardar filtros actuales' }).click();
  await expect(page.getByText('Vista guardada', { exact: true })).toBeVisible();
  await page.keyboard.press('Escape');
  await page.reload();
  await page.getByLabel('Vistas guardadas', { exact: true }).selectOption({ label: view });
  await expect(page.getByLabel('Filtrar por estado')).toHaveValue('pending');
  await page.getByRole('button', { name: 'Administrar vistas guardadas' }).click();
  await page.getByRole('button', { name: 'Eliminar vista ' + view, exact: true }).click();
  await page.getByRole('button', { name: 'Eliminar vista', exact: true }).click();
  await expect(
    page.getByRole('button', { name: 'Eliminar vista ' + view, exact: true }),
  ).toHaveCount(0);
  await expect(page.getByRole('alertdialog')).toHaveCount(0);
  await page.keyboard.press('Escape');
  await page.getByLabel('Filtrar por estado').selectOption('open');
  await page
    .getByRole('button')
    .filter({ has: page.getByText('Ana Martínez', { exact: true }) })
    .click();
  await page.getByRole('button', { name: 'Buscar mensajes', exact: true }).click();
  await page.getByLabel('Buscar en esta conversación').fill('inexistente-e2e');
  await expect(page.getByText('No hay mensajes que coincidan.', { exact: true })).toBeVisible();
  await page.getByLabel('Buscar en esta conversación').fill('próxima consulta');
  await expect(page.locator('.bubble')).toHaveCount(1);
  await page.getByRole('button', { name: 'Mostrar archivos', exact: true }).click();
  await expect(
    page.getByText('No hay archivos en los mensajes cargados.', { exact: true }),
  ).toBeVisible();
  await page.getByRole('button', { name: 'Mostrar archivos', exact: true }).click();
  await page.getByRole('button', { name: 'Buscar mensajes', exact: true }).click();
});

test('admin creates and applies an internal macro', async ({ page }, info) => {
  await login(page);
  await nav(page, 'Bandeja de entrada');
  await page.getByLabel('Filtrar por estado').selectOption('');
  await page
    .getByRole('button')
    .filter({ has: page.getByText('Ana Martínez', { exact: true }) })
    .click();
  await page.getByRole('button', { name: 'Acciones rápidas', exact: true }).click();
  await page.getByRole('button', { name: 'Administrar macros', exact: true }).click();
  const macro = 'Revisión E2E ' + info.project.name + Date.now();
  await page.getByLabel('Nombre de macro', { exact: true }).fill(macro);
  await page.getByLabel('Cambiar prioridad', { exact: true }).selectOption('high');
  await page.getByLabel('Nota interna de la macro').fill('Nota macro ' + macro);
  await page.getByRole('button', { name: 'Crear macro', exact: true }).click();
  await expect(page.getByText('Macro creada', { exact: true })).toBeVisible();
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Acciones rápidas', exact: true }).click();
  await page.getByLabel('Procedimiento', { exact: true }).selectOption({ label: macro });
  await expect(page.getByText('Prioridad: Alta', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Aplicar a esta conversación', exact: true }).click();
  await expect(page.getByText('Macro aplicada', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Acciones rápidas', exact: true }).click();
  await page.getByRole('button', { name: 'Administrar macros', exact: true }).click();
  await page.getByRole('button', { name: 'Eliminar macro ' + macro, exact: true }).click();
  await page.getByRole('button', { name: 'Eliminar macro', exact: true }).click();
  await expect(
    page.getByRole('button', { name: 'Eliminar macro ' + macro, exact: true }),
  ).toHaveCount(0);
  // The confirmation has to finish closing before Escape closes the macro dialog behind it.
  await expect(page.getByRole('alertdialog')).toHaveCount(0);
  await page.keyboard.press('Escape');
  await nav(page, 'Actividad');
  await expect(page.getByText('Nota macro ' + macro, { exact: true })).toBeVisible();
});

// ---- Lote B (docs/mejoras-revision-2026-10.md, MR-17…MR-30). The API is simulated with page.route
// in the shapes the backend sends; the session is the real Keycloak login.
type Page = import('@playwright/test').Page;
const PATIENT = '7d1f0a52-5f3c-4f7e-9f0e-2b8a1c9d0e11';
const synthetic = (name: string, extra: { patientId?: string } = {}) => ({
  id: crypto.randomUUID(),
  name,
  phone: '5037' + Math.floor(1000000 + Math.random() * 8999999),
  email: '',
  tags: '',
  lifecycleStage: 'lead',
  ...extra,
});
const channel = {
  id: 'b3c1d2e4-0000-4000-8000-000000000001',
  name: 'WhatsApp principal',
  phoneNumberId: '1234567890',
  coexistence: false,
  enabled: true,
};
function chat(name: string, conversation: Record<string, unknown> = {}) {
  const contact = synthetic(name);
  return {
    unreadCount: 0,
    contact,
    channel,
    conversation: {
      id: crypto.randomUUID(),
      contactId: contact.id,
      channelId: channel.id,
      status: 'human',
      state: 'open',
      priority: 'normal',
      labels: '',
      revision: 1,
      summary: '',
      lastMessage: 'Hola',
      updatedAt: '2026-10-17T15:00:00Z',
      ...conversation,
    },
  };
}
async function serveInbox(page: Page, chats: ReturnType<typeof chat>[], urls: string[] = []) {
  await page.route('**/api/crm/conversations?*', (route) => {
    urls.push(route.request().url());
    return route.fulfill({ json: chats });
  });
  await page.route('**/api/crm/conversations/*/messages', (route) =>
    route.request().method() === 'GET' ? route.fulfill({ json: [] }) : route.fallback(),
  );
  await page.route('**/api/crm/activities?*', (route) => route.fulfill({ json: [] }));
}
/** Counts the requests a route receives; `fulfill` answers them. */
async function counter(page: Page, url: string, method: string, json: unknown = {}) {
  const calls: unknown[] = [];
  await page.route(url, (route) => {
    if (route.request().method() !== method) return route.fallback();
    calls.push(route.request().postDataJSON());
    return route.fulfill({ json });
  });
  return calls;
}

test('MR-17 pickers and cards find the patient on the server, past the 100 on page 1', async ({
  page,
}) => {
  // Every contact is linked: the first page of linked ones still does not hold the one we look for.
  const recent = Array.from({ length: 100 }, (_, i) =>
    synthetic('Reciente ' + i, { patientId: crypto.randomUUID() }),
  );
  const old = synthetic('Paciente Antigua', { patientId: PATIENT });
  const reads: URL[] = [];
  await page.route('**/api/crm/contacts?*', (route) => {
    const url = new URL(route.request().url());
    reads.push(url);
    const q = url.searchParams.get('q')?.toLowerCase();
    const ids = url.searchParams.getAll('id');
    const all = [...recent, old].filter(
      (c) => url.searchParams.get('linked') !== 'true' || c.patientId,
    );
    return route.fulfill({
      json: url.searchParams.has('patientId')
        ? all.filter((c) => c.patientId === url.searchParams.get('patientId'))
        : ids.length
          ? all.filter((c) => ids.includes(c.id))
          : q
            ? all.filter((c) => c.name.toLowerCase().includes(q))
            : recent,
    });
  });
  await page.route('**/api/crm/opportunities', (route) =>
    route.request().method() === 'GET'
      ? route.fulfill({
          json: [{ id: 'o-1', title: 'Control anual', contactId: old.id, value: 0, stage: 'new' }],
        })
      : route.fallback(),
  );
  const start = '2026-10-19T15:00:00Z';
  await page.route('**/api/crm/hospital/agenda?*', (route) =>
    route.fulfill({
      json: {
        rows: [
          {
            appointmentId: 'a-1',
            patientId: PATIENT,
            displayName: 'Paciente Antigua',
            scheduledStart: start,
            durationMinutes: 30,
            status: 'booked',
            clinicianName: 'Dra. Sintética',
            placeName: 'Consultorio 1',
            overlaps: false,
          },
        ],
      },
    }),
  );
  await page.route('**/api/crm/hospital/availability?*', (route) =>
    route.fulfill({
      json: {
        professionals: [
          {
            clinicianId: 'd-1',
            clinicianName: 'Dra. Sintética',
            defaultDurationMinutes: 30,
            days: [
              { slots: [{ startsAt: start, durationMinutes: 30, offered: true, takenBy: 0 }] },
            ],
          },
        ],
      },
    }),
  );
  const appointments = await counter(page, '**/api/crm/hospital/appointments', 'POST');
  await login(page);
  await page.goto('/?view=opportunities');
  await expect(page.locator('.kanban-column').filter({ hasText: 'Control anual' })).toContainText(
    'Paciente Antigua',
  );
  expect(reads.some((u) => u.searchParams.getAll('id').includes(old.id))).toBe(true);
  await page.goto('/?view=calendar');
  await page.getByRole('button', { name: 'Nueva cita', exact: true }).click();
  const picker = page.getByLabel('Paciente vinculado', { exact: true });
  await expect(picker.locator('option', { hasText: 'Paciente Antigua' })).toHaveCount(0);
  await page.getByLabel('Buscar paciente vinculado').fill('antigua');
  await picker.selectOption({ label: 'Paciente Antigua' });
  expect(
    reads.some(
      (u) => u.searchParams.get('q') === 'antigua' && u.searchParams.get('linked') === 'true',
    ),
  ).toBe(true);
  await page.getByLabel('Doctor', { exact: true }).selectOption('d-1');
  await page.getByLabel('Horario disponible').selectOption(start);
  await page.getByRole('button', { name: 'Confirmar cita', exact: true }).click();
  await expect.poll(() => appointments.length).toBe(1);
  expect(appointments[0]).toMatchObject({ action: 'create', contactId: old.id });
  await page.getByRole('button', { name: 'Cancelar', exact: true }).click();
  await page.getByLabel('Motivo de la cancelación').selectOption('patient-requested');
  await page.getByRole('button', { name: 'Confirmar cancelación', exact: true }).click();
  await expect.poll(() => appointments.length).toBe(2);
  expect(appointments[1]).toMatchObject({ action: 'cancel', contactId: old.id });
  expect(reads.some((u) => u.searchParams.get('patientId') === PATIENT)).toBe(true);
  await expect(page.getByText(/Vincula este paciente/)).toHaveCount(0);
});

test('MR-18 the attention mode travels as status=; an empty filtered list offers to clear filters', async ({
  page,
}) => {
  const urls: string[] = [];
  await serveInbox(page, [], urls);
  await login(page);
  await nav(page, 'Bandeja de entrada');
  await page.getByRole('button', { name: 'Personas', exact: true }).click();
  await expect
    .poll(() => urls.some((u) => new URL(u).searchParams.get('status') === 'human'))
    .toBe(true);
  await expect(page.getByText('Sin resultados con estos filtros', { exact: true })).toBeVisible();
  await expect(page.getByText('Todo al día', { exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'Quitar filtros', exact: true }).click();
  await expect(page.getByLabel('Filtrar por estado')).toHaveValue('open');
  await expect(page.getByRole('button', { name: 'Todas', exact: true })).toHaveAttribute(
    'aria-pressed',
    'true',
  );
  await expect(page.getByText('Todo al día', { exact: true })).toBeVisible();
  await page.unroute('**/api/crm/conversations?*');
  await serveInbox(
    page,
    Array.from({ length: 100 }, (_, i) => chat('Paciente ' + i)),
  );
  // The full page has to be on screen before searching it, not the previous empty poll.
  await page.getByRole('button', { name: 'Actualizar conversaciones' }).click();
  await expect(page.getByRole('heading', { name: 'Conversaciones 100' })).toBeVisible();
  await page.getByLabel('Buscar', { exact: true }).fill('Paciente 7');
  await expect(
    page.getByText('La búsqueda por nombre solo revisa las 100 conversaciones de esta página.'),
  ).toBeVisible();
});

test('MR-19 bulk assignment has no default target, asks before removing the owner and keeps the selection on 409', async ({
  page,
}) => {
  await serveInbox(page, [chat('Ana Sintética'), chat('Carlos Sintético')]);
  await page.route('**/api/crm/members/workload', (route) =>
    route.fulfill({
      json: {
        unassigned: 2,
        members: [
          {
            subject: 'm-1',
            name: 'Rosa Sintética',
            role: 'agent',
            disabled: false,
            open: 0,
            pending: 0,
            snoozed: 0,
          },
        ],
      },
    }),
  );
  const assigned: unknown[] = [];
  await page.route('**/api/crm/conversations/assign', (route) => {
    assigned.push(route.request().postDataJSON());
    return route.fulfill({ status: 409, json: { title: 'Otra persona cambió la conversación' } });
  });
  await login(page);
  await nav(page, 'Bandeja de entrada');
  const ana = page.getByLabel('Seleccionar conversación de Ana Sintética', { exact: true });
  await ana.check();
  const assign = page.getByRole('button', { name: 'Asignar selección', exact: true });
  await expect(page.getByLabel('Responsable de la selección')).toHaveValue('');
  await expect(assign).toBeDisabled();
  await page.getByLabel('Responsable de la selección').selectOption('none');
  await assign.click();
  await page.getByRole('alertdialog').getByRole('button', { name: 'Cancelar' }).click();
  expect(assigned).toHaveLength(0);
  await page.getByLabel('Responsable de la selección').selectOption('m-1');
  await assign.click();
  await expect(page.getByText('Otra persona cambió la conversación')).toBeVisible();
  expect(assigned).toHaveLength(1);
  await expect(ana).toBeChecked();
});

test('MR-20 activating the agent and deleting a view, a macro or a saved reply ask first', async ({
  page,
}) => {
  const human = chat('Ana Sintética');
  await serveInbox(page, [human]);
  await page.route('**/api/crm/inbox-views', (route) =>
    route.request().method() === 'GET'
      ? route.fulfill({ json: [{ id: 'v-1', name: 'Urgentes', filters: '{}' }] })
      : route.fallback(),
  );
  await page.route('**/api/crm/macros', (route) =>
    route.request().method() === 'GET'
      ? route.fulfill({
          json: [
            {
              id: 'mc-1',
              name: 'Revisión',
              state: 'pending',
              priority: null,
              labels: '',
              note: '',
              takeOwnership: false,
            },
          ],
        })
      : route.fallback(),
  );
  await page.route('**/api/crm/saved-replies', (route) =>
    route.request().method() === 'GET'
      ? route.fulfill({ json: [{ id: 'r-1', title: 'Horario', body: 'Atendemos de 7 a 18.' }] })
      : route.fallback(),
  );
  const agent = await counter(page, '**/api/crm/conversations/' + human.conversation.id, 'PATCH');
  const views = await counter(page, '**/api/crm/inbox-views/v-1', 'DELETE');
  const macros = await counter(page, '**/api/crm/macros/mc-1', 'DELETE');
  const replies = await counter(page, '**/api/crm/saved-replies/r-1', 'DELETE');
  await login(page);
  await nav(page, 'Bandeja de entrada');
  async function confirmOnce(open: () => Promise<void>, action: string, calls: unknown[]) {
    await open();
    await page.getByRole('alertdialog').getByRole('button', { name: 'Cancelar' }).click();
    expect(calls).toHaveLength(0);
    await open();
    await page.getByRole('alertdialog').getByRole('button', { name: action }).click();
    await expect.poll(() => calls.length).toBe(1);
    await expect(page.getByRole('alertdialog')).toHaveCount(0);
  }
  // Saved views live in the conversation list, which an open chat replaces on mobile: first.
  await page.getByRole('button', { name: 'Administrar vistas guardadas' }).click();
  await confirmOnce(
    () => page.getByRole('button', { name: 'Eliminar vista Urgentes', exact: true }).click(),
    'Eliminar vista',
    views,
  );
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await page.locator('.conversation-card').filter({ hasText: 'Ana Sintética' }).click();
  await confirmOnce(
    () => page.getByRole('button', { name: 'Activar agente' }).click(),
    'Activar agente',
    agent,
  );
  await page.getByRole('button', { name: 'Acciones rápidas', exact: true }).click();
  await page.getByRole('button', { name: 'Administrar macros', exact: true }).click();
  await confirmOnce(
    () => page.getByRole('button', { name: 'Eliminar macro Revisión', exact: true }).click(),
    'Eliminar macro',
    macros,
  );
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Administrar respuestas guardadas' }).click();
  await confirmOnce(
    () => page.getByRole('button', { name: 'Eliminar respuesta Horario', exact: true }).click(),
    'Eliminar respuesta',
    replies,
  );
});

const settings = {
  name: 'Hospital sintético',
  guide: 'Guía sintética',
  emergencyPhone: '',
  emergencyWhatsApp: false,
  timeZone: 'America/El_Salvador',
  agentEnabled: true,
  googleConnected: false,
  hospitalConfigured: true,
  kapsoConfigured: true,
  aiConfigured: true,
  sendEnabled: true,
  aiModel: 'synthetic',
};
test('MR-21 saving the guide keeps the agentEnabled the server sent', async ({ page }) => {
  await page.route('**/api/crm/settings', (route) =>
    route.request().method() === 'GET' ? route.fulfill({ json: settings }) : route.fallback(),
  );
  const saved = await counter(page, '**/api/crm/settings', 'PUT');
  await login(page);
  await page.goto('/?view=settings');
  await expect(page.locator('form').getByLabel('Atención automática', { exact: true })).toHaveCount(
    0,
  );
  await page.getByLabel('Guía de atención').fill('Guía sintética editada');
  // The attention panel below has its own «Guardar cambios»: use the guide's form.
  await page
    .locator('form')
    .filter({ has: page.getByLabel('Guía de atención') })
    .getByRole('button', { name: 'Guardar cambios', exact: true })
    .click();
  await expect.poll(() => saved.length).toBe(1);
  expect(saved[0]).toMatchObject({ guide: 'Guía sintética editada', agentEnabled: true });
});

// The hospital is in Madrid: neither the browser's zone (Tokyo) nor the code's fallback (San Salvador).
// At 5cce865 the agent timeline printed the date in the browser zone and the time in San Salvador,
// so these two rows read «18 oct · 10:30 a. m.» and «17 oct · 05:30 p. m.»: both assertions fail there.
test.describe('MR-23 a browser in Tokyo, a hospital in Madrid', () => {
  test.use({ timezoneId: 'Asia/Tokyo' });
  test('agent timeline dates and times use the hospital zone with es-SV', async ({ page }) => {
    await page.route('**/api/crm/me', async (route) => {
      const me = await (await route.fetch()).json();
      return route.fulfill({
        json: { ...me, tenant: { ...me.tenant, timeZone: 'Europe/Madrid' } },
      });
    });
    await page.route('**/api/crm/activities', (route) =>
      route.fulfill({
        json: [
          // 16:30Z: 18:30 on the 17th in Madrid, already the 18th in Tokyo.
          {
            id: 'a-day',
            actor: 'Agente',
            actorRole: 'agent_ai',
            kind: 'agent_tool',
            body: 'Acción sintética de la tarde',
            createdAt: '2026-10-17T16:30:00Z',
          },
          // 23:30Z: 01:30 on the 17th in Madrid, 17:30 on the 16th in San Salvador.
          {
            id: 'a-night',
            actor: 'Agente',
            actorRole: 'agent_ai',
            kind: 'agent_tool',
            body: 'Acción sintética de la madrugada',
            createdAt: '2026-10-16T23:30:00Z',
          },
        ],
      }),
    );
    await login(page);
    await page.goto('/?view=agent');
    const row = (body: string) =>
      page.locator('div.border-l').filter({ hasText: body }).locator('small');
    await expect(row('Acción sintética de la tarde')).toHaveText(/^17 oct\.? · 06:30\s?p\.\s?m\.$/);
    await expect(row('Acción sintética de la madrugada')).toHaveText(
      /^17 oct\.? · 01:30\s?a\.\s?m\.$/,
    );
  });
});

test('MR-24 views are history entries and internal links do not reload', async ({ page }) => {
  await login(page);
  await nav(page, 'Bandeja de entrada');
  await nav(page, 'Agenda');
  await expect(page).toHaveURL(/view=calendar/);
  await page.goBack();
  await expect(page).toHaveURL(/view=inbox/);
  await expect(
    page.getByRole('heading', { name: 'Bandeja de entrada', exact: true }),
  ).toBeVisible();
  await page.goto('/?view=settings');
  await page.evaluate(() => Object.assign(window, { sameDocument: true }));
  await page.getByRole('link', { name: 'Administrar equipo y conversaciones ↗' }).click();
  await expect(page).toHaveURL(/view=team/);
  expect(await page.evaluate(() => 'sameDocument' in window)).toBe(true);
});

test('MR-25 without permission to reply the composer text and attachment are disabled', async ({
  page,
}) => {
  await serveInbox(page, [chat('Ana Sintética', { assignedTo: 'otra-persona' })]);
  await login(page, 'agent');
  await nav(page, 'Bandeja de entrada');
  await page.locator('.conversation-card').filter({ hasText: 'Ana Sintética' }).click();
  await expect(page.getByLabel('Mensaje al paciente')).toBeDisabled();
  await expect(page.locator('label[title="Adjuntar archivo"] input')).toBeDisabled();
});

test('MR-26 no autoscroll while reading above; the 200-message cap is announced', async ({
  page,
}) => {
  const ana = chat('Ana Sintética');
  await serveInbox(page, [ana]);
  const message = (i: number) => ({
    id: 'msg-' + i,
    sender: i % 2 ? 'human' : 'patient',
    body: 'Mensaje sintético ' + i,
    type: 'text',
    status: 'read',
    createdAt: new Date(Date.UTC(2026, 9, 17, 12, 0, i)).toISOString(),
    receivedAt: new Date(Date.UTC(2026, 9, 17, 12, 0, i)).toISOString(),
  });
  let count = 200;
  await page.route('**/api/crm/conversations/*/messages', (route) =>
    route.request().method() === 'GET'
      ? route.fulfill({ json: Array.from({ length: count }, (_, i) => message(i)) })
      : route.fallback(),
  );
  await page.route('**/api/crm/conversations/*/read', (route) => route.fulfill({ json: {} }));
  await login(page);
  await nav(page, 'Bandeja de entrada');
  await page.locator('.conversation-card').filter({ hasText: 'Ana Sintética' }).click();
  await expect(page.getByText('Se muestran los últimos 200 mensajes.')).toBeVisible();
  const list = page.locator('.message-list');
  await list.evaluate((el) => el.scrollTo({ top: 0 }));
  await page.waitForTimeout(200); // the scroll event marks the reader as away from the end
  const before = await list.evaluate((el) => el.scrollTop);
  count = 201;
  await expect(page.getByText('Mensaje sintético 200', { exact: true })).toBeAttached({
    timeout: 10000,
  });
  expect(await list.evaluate((el) => el.scrollTop)).toBe(before);
});

test('MR-27 the appointment dialog day does not move the agenda day', async ({ page }) => {
  await page.route('**/api/crm/hospital/agenda?*', (route) =>
    route.fulfill({ json: { rows: [] } }),
  );
  await page.route('**/api/crm/hospital/availability?*', (route) =>
    route.fulfill({ json: { professionals: [] } }),
  );
  await login(page);
  await page.goto('/?view=calendar');
  const agendaDay = await page.getByLabel('Fecha de la agenda').inputValue();
  await page.getByRole('button', { name: 'Nueva cita', exact: true }).click();
  await page.getByLabel('Fecha', { exact: true }).fill('2030-01-15');
  await page.keyboard.press('Escape');
  await expect(page.getByLabel('Fecha de la agenda')).toHaveValue(agendaDay);
});

test('MR-28 a failed settings read offers «Reintentar»', async ({ page }) => {
  let fail = true;
  await page.route('**/api/crm/settings', (route) =>
    route.request().method() !== 'GET'
      ? route.fallback()
      : fail
        ? route.fulfill({ status: 500, json: { title: 'Error sintético' } })
        : route.fulfill({ json: settings }),
  );
  await login(page);
  await page.goto('/?view=settings');
  await expect(page.getByText(/No pudimos leer la configuración/)).toBeVisible();
  fail = false;
  await page.getByRole('button', { name: 'Reintentar', exact: true }).click();
  await expect(
    page.getByRole('heading', { name: 'Tu hospital y su guía de atención' }),
  ).toBeVisible();
});

test('MR-29 «Inicio», distinct icons, aria-current, and settings link to «Mi hospital»', async ({
  page,
}) => {
  await login(page);
  await openMenu(page);
  await expect(page.getByRole('button', { name: 'Inicio', exact: true })).toHaveAttribute(
    'aria-current',
    'page',
  );
  const icon = (name: string) =>
    page.getByRole('button', { name, exact: true }).locator('svg').getAttribute('class');
  expect(await icon('Equipo')).not.toBe(await icon('Contactos'));
  await page.goto('/?view=settings');
  await openMenu(page);
  await expect(page.getByRole('button', { name: 'Configuración', exact: true })).toHaveAttribute(
    'aria-current',
    'page',
  );
  await expect(page.getByRole('button', { name: 'Inicio', exact: true })).not.toHaveAttribute(
    'aria-current',
    'page',
  );
  await page.keyboard.press('Escape'); // closes the menu sheet on mobile; a no-op on desktop
  await expect(page.getByRole('heading', { name: 'Mi hospital', exact: true })).toHaveCount(0);
  await page.evaluate(() => Object.assign(window, { sameDocument: true }));
  await page.getByRole('link', { name: 'Revisar la conexión en «Mi hospital»' }).click();
  await expect(page).toHaveURL(/view=hospital/);
  // The «Mi hospital» view is on screen, reached without reloading the page.
  await expect(page.getByRole('heading', { name: 'Mi hospital', level: 1 })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Mi hospital', level: 2 })).toBeVisible();
  expect(await page.evaluate(() => 'sameDocument' in window)).toBe(true);
});

test('MR-30 a column preselects its stage, says «Sin seguimientos», and agent activity is the agent’s', async ({
  page,
}) => {
  await page.route('**/api/crm/opportunities', (route) =>
    route.request().method() === 'GET' ? route.fulfill({ json: [] }) : route.fallback(),
  );
  await page.route('**/api/crm/activities', (route) =>
    route.fulfill({
      json: [
        {
          id: 'a-1',
          actor: 'Agente',
          actorRole: 'agent_ai',
          kind: 'agent_tool',
          body: 'Herramienta sintética del agente',
          createdAt: '2026-10-17T15:00:00Z',
        },
        {
          id: 'a-2',
          actor: 'Rosa Sintética',
          actorRole: 'agent',
          kind: 'handoff',
          body: 'Traspaso hecho por una persona',
          createdAt: '2026-10-17T15:01:00Z',
        },
      ],
    }),
  );
  await login(page);
  await page.goto('/?view=opportunities');
  await expect(page.getByText('Sin seguimientos', { exact: true })).toHaveCount(5);
  await page.getByRole('button', { name: 'Agregar seguimiento en Agendado', exact: true }).click();
  await expect(page.getByLabel('Etapa', { exact: true })).toHaveValue('scheduled');
  await page.keyboard.press('Escape');
  await page.goto('/?view=agent');
  await expect(page.getByText('Herramienta sintética del agente')).toBeVisible();
  await expect(page.getByText('Traspaso hecho por una persona')).toHaveCount(0);
});
