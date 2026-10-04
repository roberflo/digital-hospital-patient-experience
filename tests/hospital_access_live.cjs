// Same person, same hospital, in both products. Synthetic local Hospital; read-only.
// Regression: with Hospital still signed in as hospital A, «Abrir Hospital» from hospital C's
// Recepción opened hospital A. Links now enter through Keycloak, so they open C as the same user.
const { chromium } = require('../frontend/node_modules/playwright');
const { readFileSync } = require('node:fs');
const path = require('node:path');
const pw = readFileSync(path.resolve(__dirname, '../../Hospital/.env'), 'utf8').split('\n').find(l => l.startsWith('KC_DEV_USERS_PASSWORD=')).slice('KC_DEV_USERS_PASSWORD='.length).trim();
const settle = async page => { await page.waitForLoadState('networkidle').catch(() => {}); return (await page.locator('body').innerText()).replace(/\s+/g, ' '); };
(async () => {
  const browser = await chromium.launch(); const ctx = await browser.newContext();
  const hospital = await ctx.newPage();
  await hospital.goto('http://localhost:3210/es/login');
  await hospital.locator('input[type=text], input[type=email]').first().fill('dev-administrador-a');
  await hospital.locator('input[type=password]').fill(pw); await hospital.locator('button[type=submit]').click();
  await hospital.waitForURL(u => !u.pathname.includes('/login'));
  if (!(await settle(hospital)).includes('Aguilar')) throw Error('Precondition: Hospital signed in as hospital A');
  const page = await ctx.newPage();
  await page.goto('http://localhost:3215/login'); await page.getByRole('button', { name: 'Continuar con mi cuenta del hospital' }).click();
  await page.locator('#username').fill('dev-administrador-c'); await page.locator('#password').fill(pw); await page.locator('#kc-login').click();
  await page.waitForURL(u => u.origin === 'http://localhost:3215' && !/^\/(login|api)/.test(u.pathname));
  for (const [api, label] of [['/api/crm/hospital/connection', 'Mi hospital'], ['/api/crm/commercial/settings', 'Comercial']]) {
    const { hospitalUrl } = await (await page.request.get('http://localhost:3215' + api)).json();
    if (!hospitalUrl) throw Error(label + ': no Hospital link');
    await hospital.goto(hospitalUrl); const text = await settle(hospital);
    if (await hospital.locator('input[type=password]').count()) throw Error(label + ': Hospital asked for credentials again');
    if (!text.includes('Castillo') || text.includes('Aguilar')) throw Error(label + ': opened a different hospital than the one signed in to Recepción');
    console.log('PASS ' + label + ' opens Hospital as the same person in the same hospital, without credentials (' + new URL(hospital.url()).pathname + ')');
  }
  // The other direction: Hospital's «Recepción» door opens Recepción as the person signed in to
  // Hospital, even when Recepción (and Keycloak) still remember someone from another hospital.
  const back = await browser.newContext();
  const front = await back.newPage();           // Recepción + Keycloak remember hospital A
  await front.goto('http://localhost:3215/login'); await front.getByRole('button', { name: 'Continuar con mi cuenta del hospital' }).click();
  await front.locator('#username').fill('dev-administrador-a'); await front.locator('#password').fill(pw); await front.locator('#kc-login').click();
  await front.waitForURL(u => u.origin === 'http://localhost:3215' && !/^\/(login|api)/.test(u.pathname));
  const clinic = await back.newPage();          // Hospital signed in as hospital C, its own form
  await clinic.goto('http://localhost:3210/es/login');
  await clinic.locator('input[type=text], input[type=email]').first().fill('dev-administrador-c');
  await clinic.locator('input[type=password]').fill(pw); await clinic.locator('button[type=submit]').click();
  await clinic.waitForURL(u => !u.pathname.includes('/login')); await settle(clinic);
  await clinic.locator('[data-testid=account-trigger]').first().click();
  const door = clinic.locator('[data-testid=account-recepcion]');
  const href = await door.getAttribute('href');
  if (!/^http:\/\/localhost:3215\/login\?as=[0-9a-f-]{36}$/.test(href || '')) throw Error('Hospital door: unexpected link');
  await front.goto(href);
  await front.locator('#password').waitFor();   // someone else is remembered: Keycloak must ask
  // Keycloak re-asks the remembered person's password with the username locked; restart frees it.
  if (!(await front.locator('#username').isVisible())) await front.locator('#reset-login').click();
  await front.locator('#username').fill('dev-administrador-c'); await front.locator('#password').fill(pw); await front.locator('#kc-login').click();
  await front.waitForURL(u => u.origin === 'http://localhost:3215' && u.pathname === '/');
  const me = await (await front.request.get('http://localhost:3215/api/crm/me')).json();
  if (me.tenant.id !== 'cccccccc-cccc-4ccc-8ccc-cccccccccccc' || !me.name.includes('Castillo')) throw Error('Hospital door: Recepción opened as someone else');
  console.log('PASS Hospital\'s Recepción door replaces another hospital\'s session with the person signed in to Hospital');
  await front.goto(href); await front.waitForURL(u => u.origin === 'http://localhost:3215' && u.pathname === '/');
  console.log('PASS the same door, once Recepción is that person, enters without credentials');
  await browser.close();
})().catch(e => { console.error('FAIL ' + String(e.message || e).split('\n')[0]); process.exit(1); });
