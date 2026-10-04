// When a person signs out of one product, how long until the other stops working? ~8 minutes.
// A/B: Hospital entered through Recepción's link (shared Keycloak SSO). C/D: Hospital entered with its own form.
// Synthetic local users only. Fails if a shared Keycloak session (A, B) outlives the sign-out by more than 7 minutes. Prints no secrets.
const { chromium } = require('../frontend/node_modules/playwright');
const { readFileSync } = require('node:fs');
const path = require('node:path');
const pw = readFileSync(path.resolve(__dirname, '../../Hospital/.env'), 'utf8').split('\n').find(l => l.startsWith('KC_DEV_USERS_PASSWORD=')).slice('KC_DEV_USERS_PASSWORD='.length).trim();
const USER = 'dev-administrador-c', R = 'http://localhost:3215', H = 'http://localhost:3210';
const LIMIT = 420, STEP = 20;
const sleep = ms => new Promise(r => setTimeout(r, ms));
const settle = async page => { await page.waitForLoadState('networkidle').catch(() => {}); return (await page.locator('body').innerText()).replace(/\s+/g, ' '); };

async function recepcionLogin(ctx) {
  const page = await ctx.newPage();
  await page.goto(R + '/login'); await page.getByRole('button', { name: 'Continuar con mi cuenta del hospital' }).click();
  await page.locator('#username').fill(USER); await page.locator('#password').fill(pw); await page.locator('#kc-login').click();
  await page.waitForURL(u => u.origin === R && !/^\/(login|api)/.test(u.pathname), { timeout: 60000 }).catch(async e => {
    const msg = await page.locator('#input-error, .alert-error, [class*=error]').first().innerText({ timeout: 1000 }).catch(() => '');
    throw Error('stuck at ' + new URL(page.url()).origin + new URL(page.url()).pathname + (msg ? ' [' + msg.split('\n')[0] + ']' : ''));
  });
  return page;
}
async function hospitalViaRecepcion(ctx, front) {
  const { hospitalUrl } = await (await front.request.get(R + '/api/crm/hospital/connection')).json();
  if (!hospitalUrl) throw Error('no Hospital link');
  const h = await ctx.newPage(); await h.goto(hospitalUrl); return h;
}
async function hospitalOwnForm(ctx) {
  const h = await ctx.newPage(); await h.goto(H + '/es/login');
  await h.locator('input[type=text], input[type=email]').first().fill(USER);
  await h.locator('input[type=password]').fill(pw); await h.locator('button[type=submit]').click();
  await h.waitForURL(u => !u.pathname.includes('/login')); return h;
}
async function expectHospitalPerson(h, label) {
  const t = await settle(h);
  if (!t.includes('Castillo') || await h.locator('input[type=password]').count()) throw Error(label + ': Hospital does not show the person');
}
async function signOutRecepcion(front) {
  await front.goto(R + '/'); await settle(front);
  await front.getByRole('button', { name: 'Cerrar sesión' }).first().click();
  await front.waitForTimeout(4000); await front.waitForLoadState('networkidle').catch(() => {});
}
async function signOutHospital(h) {
  await h.goto(H + '/es'); await settle(h);
  await h.locator('[data-testid=account-trigger]').first().click();
  await h.locator('[data-testid=account-sign-out]').click();
  await h.waitForTimeout(1500);
  const dlg = h.locator('[role=alertdialog], [role=dialog]').last();
  if (await dlg.count()) { // confirmation step, if any
    await dlg.getByRole('button', { name: /cerrar|salir|sign out|confirm|aceptar|sí|si\b/i }).last().click();
  }
  await h.waitForTimeout(4000); await h.waitForLoadState('networkidle').catch(() => {});
}
async function hospitalDead(h) {
  await h.goto(H + '/es').catch(() => {}); await h.waitForLoadState('networkidle').catch(() => {});
  const u = new URL(h.url());
  const pwField = await h.locator('input[type=password]').count();
  const dead = /login|auth|realms/.test(u.pathname) || u.origin !== H || pwField > 0;
  return { dead, what: u.origin === H ? u.pathname : 'keycloak:' + u.pathname.split('/').slice(0, 4).join('/') };
}
async function recepcionDead(front) {
  const s = (await front.request.get(R + '/api/crm/me')).status();
  return { dead: s === 401, what: 'HTTP ' + s };
}
async function poll(check, tag) {
  const t0 = Date.now(); let last = { what: '?' };
  for (;;) {
    const e = Math.round((Date.now() - t0) / 1000);
    try { last = await check(); } catch (err) { last = { dead: false, what: 'err ' + String(err.message).split('\n')[0] }; }
    if (last.dead) return `dead after ${e} s (${last.what})`;
    if (e >= LIMIT) return `still alive after ${LIMIT} s (last: ${last.what})`;
    await sleep(STEP * 1000);
  }
}

async function scenario(browser, name, own, signOutOf, poller) {
  const ctx = await browser.newContext();
  try {
    let step = 'recepcion login';
    const front = await recepcionLogin(ctx).catch(e => { throw Error(step + ': ' + e.message); });
    step = own ? 'hospital own form' : 'hospital via link';
    const h = await (own ? hospitalOwnForm(ctx) : hospitalViaRecepcion(ctx, front)).catch(e => { throw Error(step + ': ' + e.message); });
    await expectHospitalPerson(h, name);
    step = 'sign out';
    await (signOutOf === 'recepcion' ? signOutRecepcion(front) : signOutHospital(h)).catch(e => { throw Error(step + ': ' + e.message); });
    const res = await poll(poller === 'hospital' ? () => hospitalDead(h) : () => recepcionDead(front), name);
    return `RESULT ${name}: ${res}`;
  } catch (e) {
    return `RESULT ${name}: could not drive - ${String(e.message || e).split('\n')[0]}`;
  } finally { await ctx.close().catch(() => {}); }
}

(async () => {
  const browser = await chromium.launch();
  const later = (ms, ...a) => sleep(ms).then(() => scenario(browser, ...a));
  const out = await Promise.all([
    later(0000, 'A (SSO link; sign out Recepción; poll Hospital)', false, 'recepcion', 'hospital'),
    later(6000, 'B (SSO link; sign out Hospital; poll Recepción)', false, 'hospital', 'recepcion'),
    later(12000, 'C (Hospital own form; sign out Recepción; poll Hospital)', true, 'recepcion', 'hospital'),
    later(18000, 'D (Hospital own form; sign out Hospital; poll Recepción)', true, 'hospital', 'recepcion'),
  ]);
  out.forEach(l => console.log(l));
  await browser.close();
  // The gate: one Keycloak session, one sign-out. A and B must die at the next token refresh.
  // C and D are reported only: Hospital's own form signs in without a Keycloak browser session,
  // so nothing links the two until Hospital's default login moves to the Keycloak page (AUTH-2).
  if (!out.slice(0, 2).every(l => l.includes('dead after'))) process.exitCode = 1;
})().catch(e => { console.log('RESULT error: ' + String(e.message || e).split('\n')[0]); process.exitCode = 1; }).finally(() => process.exit());
