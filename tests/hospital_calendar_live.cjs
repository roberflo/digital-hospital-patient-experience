// Read-only agenda check in an isolated browser; synthetic local Hospital C.
const { chromium } = require('../frontend/node_modules/playwright');
const { readFileSync } = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '..');
const env = Object.fromEntries(readFileSync(root+'/.env','utf8').split('\n').filter(l=>l&&!l.startsWith('#')&&l.includes('=')).map(l=>{const i=l.indexOf('=');return [l.slice(0,i),l.slice(i+1)];}));
const hospital = Object.fromEntries(readFileSync(process.env.HOSPITAL_ENV||root+'/../Hospital/.env','utf8').split('\n').filter(l=>l&&!l.startsWith('#')&&l.includes('=')).map(l=>{const i=l.indexOf('=');return [l.slice(0,i),l.slice(i+1).trim().replace(/^["']|["']$/g,'')];}));
if(env.ASPNETCORE_ENVIRONMENT!=='Development'||env.DEV_HOSPITAL_TENANT_ID!=='cccccccc-cccc-4ccc-8ccc-cccccccccccc')throw new Error('Local synthetic Hospital required');
(async()=>{
 const browser=await chromium.launch({headless:true});
 try{
  const page=await browser.newPage();await page.goto('http://localhost:3215/login');
  await page.getByRole('button',{name:'Continuar con mi cuenta del hospital'}).click();
  await page.locator('#username').fill('dev-recepcion-c');await page.locator('#password').fill(hospital.KC_DEV_USERS_PASSWORD);await page.locator('#kc-login').click();
  await page.waitForURL('http://localhost:3215/',{timeout:30000});
  const response=page.waitForResponse(r=>r.url().includes('/api/crm/hospital/agenda?'));
  await page.getByRole('button',{name:'Agenda',exact:true}).click();
  if((await response).status()!==200)throw new Error('Hospital agenda unavailable');
  await page.getByRole('button',{name:'Nueva cita',exact:true}).waitFor();
  if(await page.getByText('hospital.not_configured',{exact:false}).count())throw new Error('Agenda remains unconfigured');
  await page.screenshot({path:root+'/artifacts/hospital-calendar-live.png',fullPage:true});
  await page.getByRole('button',{name:'Cerrar sesión',exact:true}).click();
  // Signing out also ends the Keycloak session: back on /login, and Keycloak asks for credentials again.
  await page.waitForURL(u=>u.pathname==='/login');
  await page.getByRole('button',{name:'Continuar con mi cuenta del hospital'}).click();
  await page.locator('#username').waitFor();
  if((await page.request.get('http://localhost:3215/api/crm/me')).status()!==401)throw new Error('Session survived sign-out');
  console.log('PASS Hospital C calendar HTTP200, isolated tenant and sign-out ends the Hospital session');
 }finally{await browser.close();}
// First line only: Playwright's navigation log would print Keycloak URLs that carry an ID token.
})().catch(e=>{console.error(e.message.split('\n')[0]);process.exitCode=1;});
