import { test, expect } from '@playwright/test';
async function login(page: import('@playwright/test').Page) {
  await page.goto('/login');
  await page.getByLabel('Contraseña', { exact: true }).fill('demo-recepcion');
  await page.getByRole('button', { name: 'Entrar al espacio' }).click();
  await expect(page.getByRole('heading', { name: 'Dashboard', exact: true })).toBeVisible();
}
async function nav(page: import('@playwright/test').Page, name: string) {
  const menu = page.getByRole('button', { name: 'Abrir menú' });
  if (await menu.isVisible()) await menu.click();
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
  await page.getByLabel('Estado del cliente').selectOption('active');
  await page.getByRole('button', { name: 'Guardar ficha CRM', exact: true }).click();
  await expect(page.getByText('Ficha CRM actualizada', { exact: true })).toBeVisible();
  await expect(page.getByText('Cliente activo', { exact: true })).toBeVisible();
  await page.getByLabel('Estado de conversación', { exact: true }).selectOption('open');
  await expect(page.getByLabel('Estado de conversación', { exact: true })).toHaveValue('open');
  await page.getByLabel('Prioridad', { exact: true }).selectOption('normal');
  await expect(page.getByLabel('Prioridad', { exact: true })).toHaveValue('normal');
  if (await page.getByRole('button', { name: 'Cerrar detalles' }).isVisible()) {
    await page.getByRole('button', { name: 'Cerrar detalles' }).click();
  }
  await nav(page, 'Contactos');
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
  await expect(
    page.getByRole('button', { name: 'Eliminar vista ' + view, exact: true }),
  ).toHaveCount(0);
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

test('supervisor creates and applies an internal macro', async ({ page }, info) => {
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
  await expect(
    page.getByRole('button', { name: 'Eliminar macro ' + macro, exact: true }),
  ).toHaveCount(0);
  await page.keyboard.press('Escape');
  await nav(page, 'Actividad');
  await expect(page.getByText('Nota macro ' + macro, { exact: true })).toBeVisible();
});
