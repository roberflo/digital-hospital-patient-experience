import { test, expect, type Page } from '@playwright/test';
import { login as signIn, type User } from './login';
const id = 'aaa00000-0000-4000-8000-000000000001';
const login = (page: Page, user: User = 'doctor') => signIn(page, user);
// Subjects are Keycloak user ids, so the signed-in one is read rather than assumed.
const subject = async (page: Page): Promise<string> =>
  (await (await page.request.get('/api/crm/me')).json()).subject;
/** Opens a synthetic conversation; assigned to whoever is signed in unless told otherwise. */
async function inbox(page: Page, owner?: string) {
  const assigned = owner ?? (await subject(page));
  await page.route('**/api/crm/conversations?*', (r) =>
    r.fulfill({
      json: [
        {
          conversation: {
            id,
            contactId: id,
            assignedTo: assigned,
            status: 'human',
            state: 'open',
            priority: 'normal',
            labels: '',
            revision: 0,
            updatedAt: new Date().toISOString(),
          },
          contact: {
            id,
            patientId: id,
            name: 'Paciente clínico sintético',
            phone: '50370000001',
            tags: '',
          },
          channel: { id, name: 'Canal sintético', phoneNumberId: 'demo' },
          unreadCount: 0,
        },
      ],
    }),
  );
  await page.route(`**/api/crm/conversations/${id}/messages`, (r) => r.fulfill({ json: [] }));
  await page.route(`**/api/crm/conversations/${id}/read`, (r) => r.fulfill({ status: 204 }));
  await page.goto('/?view=inbox');
  await page
    .locator('.conversation-card')
    .filter({ hasText: 'Paciente clínico sintético' })
    .click();
}
test.afterEach(async ({ page }) => {
  await page.unrouteAll({ behavior: 'wait' });
});
test('doctor reads Hospital on demand with prescription, pagination, and explicit clinical states', async ({
  page,
}, info) => {
  await login(page);
  let calls = 0;
  await page.route('**/api/crm/hospital/conversations/**/clinical/**', (r) => {
    calls++;
    const url = new URL(r.request().url());
    if (url.pathname.endsWith('/timeline'))
      return r.fulfill({
        json: {
          recordOrigin: 'migrated',
          nextCursor: url.search ? null : 'next+cursor',
          items: url.search
            ? []
            : [
                {
                  entryId: id,
                  entryType: 'prescription',
                  clinicalDate: '2026-10-01',
                  signerDisplay: 'Doctora sintética',
                  state: 'signed',
                  sourceRef: { kind: 'prescription', id },
                },
              ],
        },
      });
    if (url.pathname.includes('/prescriptions/'))
      return r.fulfill({
        json: {
          state: 'signed',
          clinicalDate: '2026-10-01',
          contentWithheld: false,
          lines: [
            {
              medicationLineId: 'line',
              drugName: 'Medicamento sintético',
              doseAmount: 1,
              doseUnit: 'tablet',
              route: 'oral',
              frequencyIsSingleDose: true,
              durationDays: 1,
              specialInstructions: 'Instrucción de Hospital',
            },
          ],
        },
      });
    if (url.pathname.endsWith('/allergies')) return r.fulfill({ json: { state: 'NotAsked' } });
    return r.fulfill({
      json: {
        byCategory: { medico: { state: 'AssertedAbsent' }, familiar: { state: 'Unobtainable' } },
      },
    });
  });
  await inbox(page);
  expect(calls).toBe(0);
  await page.getByRole('button', { name: 'Expediente y recetas', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText('Expediente migrado:', { exact: false })).toBeVisible();
  await dialog.getByRole('button', { name: 'Ver receta', exact: true }).click();
  await expect(dialog.getByText('Medicamento sintético', { exact: true })).toBeVisible();
  await expect(dialog.getByText('Instrucciones: Instrucción de Hospital')).toBeVisible();
  await page.screenshot({ path: `../artifacts/clinical-${info.project.name}.png`, fullPage: true });
  await dialog.getByRole('button', { name: 'Volver al historial' }).click();
  await dialog.getByRole('button', { name: 'Siguiente página' }).click();
  await expect(
    dialog.getByText('No hay registros para esta vista en la página consultada.'),
  ).toBeVisible();
  await dialog.getByRole('button', { name: 'Antecedentes', exact: true }).click();
  await expect(dialog.getByText('Ausencia declarada', { exact: true })).toBeVisible();
  await expect(dialog.getByText('No se pudo obtener', { exact: true })).toBeVisible();
  await dialog.getByRole('button', { name: 'Alergias', exact: true }).click();
  await expect(dialog.getByText('No se ha preguntado', { exact: true })).toBeVisible();
  await dialog.getByRole('button', { name: 'Cerrar', exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
});
test('clinical access requires assignment and staff API access is denied', async ({ page }) => {
  await login(page);
  const doctor = await subject(page);
  await inbox(page, 'another-doctor');
  await expect(
    page.getByRole('button', { name: 'Expediente y recetas', exact: true }),
  ).toBeDisabled();
  await page.unrouteAll({ behavior: 'wait' });
  await login(page, 'agent');
  await inbox(page, doctor);
  await expect(page.getByRole('button', { name: 'Expediente y recetas', exact: true })).toHaveCount(
    0,
  );
  expect(
    (await page.request.get(`/api/crm/hospital/conversations/${id}/clinical/timeline`)).status(),
  ).toBe(403);
});
test('restricted prescription content stays hidden and clinical denial has a retry state', async ({
  page,
}) => {
  await login(page);
  await page.route('**/api/crm/hospital/conversations/**/clinical/timeline', (r) =>
    r.fulfill({
      json: {
        recordOrigin: 'new',
        items: [
          {
            entryId: id,
            entryType: 'prescription',
            clinicalDate: '2026-10-01',
            state: 'voided',
            sourceRef: { kind: 'prescription', id },
          },
        ],
      },
    }),
  );
  await page.route('**/api/crm/hospital/conversations/**/clinical/prescriptions/*', (r) =>
    r.fulfill({
      json: {
        state: 'voided',
        clinicalDate: '2026-10-01',
        contentWithheld: true,
        lines: [{ drugName: 'Hidden medication' }],
      },
    }),
  );
  await page.route('**/api/crm/hospital/conversations/**/clinical/allergies', (r) =>
    r.fulfill({ status: 403, json: { title: 'Denied' } }),
  );
  await inbox(page);
  await page.getByRole('button', { name: 'Expediente y recetas', exact: true }).click();
  await page.getByRole('button', { name: 'Ver receta', exact: true }).click();
  await expect(
    page.getByText('Hospital ha restringido el contenido de esta receta.'),
  ).toBeVisible();
  await expect(page.getByText('Hidden medication', { exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'Alergias', exact: true }).click();
  await expect(
    page.getByRole('alert').filter({ hasText: 'No tienes acceso clínico.' }),
  ).toBeVisible();
  await expect(page.getByText('Hospital ha restringido el contenido de esta receta.')).toHaveCount(
    0,
  );
});
