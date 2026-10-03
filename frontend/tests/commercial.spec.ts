import { test, expect, type Page } from '@playwright/test';
async function login(page: Page) {
  await page.goto('/login');
  await page.getByLabel('Contraseña', { exact: true }).fill('demo-recepcion');
  await page.getByRole('button', { name: 'Entrar al espacio' }).click();
  await expect(page.getByRole('heading', { name: 'Dashboard', exact: true })).toBeVisible();
}
const cid = '78000000-0000-4000-8000-000000000001',
  oid = '78000000-0000-4000-8000-000000000002',
  sid = '78000000-0000-4000-8000-000000000003';
const quote = {
  quoteVersion: 'server-v1',
  currency: 'USD',
  subtotal: 100,
  discountPercent: 15,
  discountAmount: 15,
  total: 85,
  lines: [{ serviceId: sid, name: 'Consulta sintética', quantity: 1, unitPrice: 100, total: 85 }],
};
test('Hospital masters are read only in Reception and services show current server prices', async ({
  page,
}, info) => {
  await page.route('**/api/crm/commercial/**', (route) => {
    const path = new URL(route.request().url()).pathname;
    const items = path.endsWith('companies')
      ? [
          {
            id: cid,
            name: 'Empresa sintética',
            active: true,
            taxId: 'NIT-TEST',
            agreement: { name: 'Convenio general', discountPercent: 15, active: true },
          },
        ]
      : [
          {
            id: sid,
            name: 'Consulta sintética',
            kind: 'consultation',
            code: 'CONS',
            price: 100,
            currency: 'USD',
          },
        ];
    return route.fulfill({
      json: path.endsWith('settings')
        ? { hospitalUrl: 'http://localhost:3210/es/commercial' }
        : { items, total: 1, page: 1, pageSize: 100 },
    });
  });
  await login(page);
  await page.goto('/?view=companies');
  await expect(page.getByText('15% de descuento')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Nueva empresa', exact: true })).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'Administrar en Hospital' })).toHaveAttribute(
    'href',
    'http://localhost:3210/es/commercial',
  );
  await page.getByRole('button', { name: 'Servicios y precios', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Consulta sintética' })).toBeVisible();
  await expect(page.getByText('$100.00', { exact: true })).toBeVisible();
  expect(
    await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),
  ).toBeTruthy();
  await page.screenshot({
    path: `../artifacts/commercial-catalogue-${info.project.name}.png`,
    fullPage: true,
  });
});
test('opportunity uses Hospital quote and requires received payment before recording purchase', async ({
  page,
}, info) => {
  let purchased = false;
  let quoteBody: unknown;
  let purchaseBody: Record<string, unknown> | undefined;
  await page.route('**/api/crm/contacts?*', (route) =>
    route.fulfill({
      json: [
        {
          id: cid,
          name: 'Contacto comercial sintético',
          phone: '50370000678',
          lifecycleStage: 'lead',
          isCustomer: false,
        },
      ],
    }),
  );
  await page.route('**/api/crm/contacts', (route) =>
    route.fulfill({
      json: [
        {
          id: cid,
          name: 'Contacto comercial sintético',
          phone: '50370000678',
          lifecycleStage: 'lead',
          isCustomer: purchased,
        },
      ],
    }),
  );
  await page.route('**/api/crm/opportunities', (route) =>
    route.fulfill({
      json: [
        {
          id: oid,
          title: 'Consulta cotizada',
          contactId: cid,
          value: purchased ? 85 : 0,
          stage: purchased ? 'won' : 'new',
          hospitalQuote: quoteBody ? JSON.stringify(quote) : null,
          hospitalPurchaseId: purchased ? 'purchase-1' : null,
        },
      ],
    }),
  );
  await page.route('**/api/crm/commercial/**', (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/quote')) {
      quoteBody = route.request().postDataJSON();
      return route.fulfill({ json: quote });
    }
    if (path.endsWith('/purchase')) {
      purchaseBody = route.request().postDataJSON();
      purchased = true;
      return route.fulfill({ json: { id: 'purchase-1', status: 'completed', quote } });
    }
    if (path.endsWith('services'))
      return route.fulfill({
        json: {
          items: [
            {
              id: sid,
              name: 'Consulta sintética',
              price: 100,
              currency: 'USD',
              kind: 'consultation',
            },
          ],
          total: 1,
          page: 1,
          pageSize: 100,
        },
      });
    return route.fulfill({
      json: path.endsWith('settings') ? {} : { items: [], total: 0, page: 1, pageSize: 100 },
    });
  });
  await login(page);
  await page.goto('/?view=opportunities');
  await page.getByRole('button', { name: 'Servicios y cotización', exact: true }).click();
  await page.getByLabel('Agregar servicio').selectOption(sid);
  await page.getByRole('button', { name: 'Cotizar con Hospital', exact: true }).click();
  await expect(page.getByText('Convenio 15%', { exact: true })).toBeVisible();
  expect(quoteBody).toEqual({ lines: [{ serviceId: sid, quantity: 1 }], companyId: null });
  const buy = page.getByRole('button', { name: 'Registrar compra pagada', exact: true });
  await expect(buy).toBeDisabled();
  await page.getByLabel('Referencia del pago').fill('RECIBO-SINTETICO');
  await page.getByRole('checkbox').check();
  await expect(buy).toBeEnabled();
  await page.screenshot({
    path: `../artifacts/commercial-quote-${info.project.name}.png`,
    fullPage: true,
  });
  await buy.click();
  await expect(
    page.getByText('Compra registrada en Hospital. Contacto convertido en cliente.', {
      exact: true,
    }),
  ).toBeVisible();
  expect(purchaseBody).toEqual({
    quoteVersion: 'server-v1',
    paymentReceived: true,
    paymentReference: 'RECIBO-SINTETICO',
  });
  await expect(
    page.getByRole('button', { name: 'Ver compra Hospital', exact: true }),
  ).toBeVisible();
});
