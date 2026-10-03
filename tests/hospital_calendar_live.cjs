// Read-only agenda check in an isolated browser; synthetic local Hospital C.
const { chromium } = require('../frontend/node_modules/playwright');
const { readFileSync } = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '..');
const env = Object.fromEntries(readFileSync(root+'/.env','utf8').split('\n').filter(l=>l&&!l.startsWith('#')&&l.includes('=')).map(l=>{const i=l.indexOf('=');return [l.slice(0,i),l.slice(i+1)];}));
if(env.ASPNETCORE_ENVIRONMENT!=='Development'||env.DEV_HOSPITAL_TENANT_ID!=='cccccccc-cccc-4ccc-8ccc-cccccccccccc')throw new Error('Local synthetic Hospital required');
(async()=>{
 const browser=await chromium.launch({headless:true});
 try{
  const page=await browser.newPage();await page.goto('http://localhost:3215/login');
  await page.getByLabel('Usuario de demostración').selectOption('hospital');
  await page.getByLabel('Contraseña',{exact:true}).fill(env.DEV_PASSWORD||'demo-recepcion');
  await page.getByRole('button',{name:'Entrar al espacio',exact:true}).click();await page.waitForURL('http://localhost:3215/');
  const response=page.waitForResponse(r=>r.url().includes('/api/crm/hospital/agenda?'));
  await page.getByRole('button',{name:'Agenda',exact:true}).click();
  if((await response).status()!==200)throw new Error('Hospital agenda unavailable');
  await page.getByRole('button',{name:'Nueva cita',exact:true}).waitFor();
  if(await page.getByText('hospital.not_configured',{exact:false}).count())throw new Error('Agenda remains unconfigured');
  await page.screenshot({path:root+'/artifacts/hospital-calendar-live.png',fullPage:true});
  await page.getByRole('button',{name:'Cerrar sesión',exact:true}).click();
  await page.getByLabel('Usuario de demostración').waitFor();
  if(await page.getByLabel('Usuario de demostración').inputValue()!=='hospital')throw new Error('Connected Hospital selection not retained');
  console.log('PASS Hospital C calendar HTTP200, isolated tenant and remembered login selection');
 }finally{await browser.close();}
})().catch(e=>{console.error(e.message);process.exitCode=1;});
