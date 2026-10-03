// Opt-in local synthetic test: shared Hospital login, encrypted connection and patient search.
const { chromium } = require('../frontend/node_modules/playwright');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const { execFileSync } = require('node:child_process');
const root=resolve(__dirname,'..');
function env(path){return Object.fromEntries(readFileSync(path,'utf8').split('\n').filter(l=>l&&!l.startsWith('#')&&l.includes('=')).map(l=>{const i=l.indexOf('=');return[l.slice(0,i),l.slice(i+1).trim().replace(/^["']|["']$/g,'')]}));}
const hospital=env(root+'/../Hospital/.env'), reception=env(root+'/.env');
if(hospital.KC_SEED_DEV_USERS!=='true'||reception.ASPNETCORE_ENVIRONMENT!=='Development')throw Error('Local synthetic environment required');
const tenant='cccccccc-cccc-4ccc-8ccc-cccccccccccc', prefix='Hospital__Tenants__'+tenant+'__';
(async()=>{
 const browser=await chromium.launch({headless:true});const page=await browser.newPage();
 try {
  await page.goto('http://localhost:3215/login');await page.getByRole('button',{name:'Continuar con mi cuenta del hospital',exact:true}).click();
  await page.locator('#username').fill('dev-administrador-c');await page.locator('#password').fill(hospital.KC_DEV_USERS_PASSWORD);await page.locator('#kc-login').click();await page.waitForURL('http://localhost:3215/',{timeout:30000});
  const me=await (await page.request.get('http://localhost:3215/api/crm/me')).json();if(me.tenant.id!==tenant||me.role!=='admin')throw Error('Shared identity not correctly bound');
  console.log('PASS Hospital administrator login uses existing hospital and role');
  await page.goto('http://localhost:3215/?view=hospital');await page.getByText('Sesión compartida activa',{exact:false}).waitFor();
  await page.getByText('Conectar o actualizar Hospital',{exact:true}).click();
  await page.getByLabel('Dirección de la aplicación Hospital').fill('http://localhost:3210');
  await page.getByLabel('Identificador de conexión').fill(reception[prefix+'ClientId']);
  await page.getByLabel('Secreto de conexión').fill(reception[prefix+'ClientSecret']);
  const saving=page.waitForResponse(r=>r.url().endsWith('/api/crm/hospital/connection')&&r.request().method()==='PUT');
  await page.getByRole('button',{name:'Comprobar y conectar',exact:true}).click();const saved=await saving;if(!saved.ok())throw Error('Connection saving HTTP '+saved.status());
  const raw=await saved.text();if(raw.includes(reception[prefix+'ClientSecret']))throw Error('Secret leak');
  await page.getByText('Conexión comprobada con la agenda de este hospital.',{exact:true}).waitFor();
  console.log('PASS verified connection saved using current service account, secret not returned');
  await page.screenshot({path:root+'/artifacts/hospital-connection-live.png',fullPage:true});
  const contacts=await (await page.request.get('http://localhost:3215/api/crm/contacts')).json();const linked=contacts.find(c=>c.patientId==='01a0a362-0dd9-7418-a987-ad9c8e67758b');if(!linked)throw Error('Synthetic linked patient fixture required');
  const fixtureName=JSON.parse(execFileSync('docker',['run','--rm','-i','--network','hospital','python:3.12-slim','python','-c',`
import json,sys,urllib.request,urllib.parse
cfg=json.load(sys.stdin)
req=urllib.request.Request('http://hospital-keycloak-1:8080/realms/hospital/protocol/openid-connect/token',data=urllib.parse.urlencode({'grant_type':'client_credentials','client_id':cfg['id'],'client_secret':cfg['secret']}).encode())
token=json.load(urllib.request.urlopen(req))['access_token']
req=urllib.request.Request('http://hospital-recepcion-api:8080/v1/patients/01a0a362-0dd9-7418-a987-ad9c8e67758b',headers={'Authorization':'Bearer '+token})
patient=json.load(urllib.request.urlopen(req));print(json.dumps(patient['givenNames']+' '+patient['familyNames']))
`],{input:JSON.stringify({id:reception[prefix+'ClientId'],secret:reception[prefix+'ClientSecret']}),encoding:'utf8'}));
  const res=await page.request.post(`http://localhost:3215/api/crm/hospital/contacts/${linked.id}/patients/search`,{headers:{Origin:'http://localhost:3215'},data:{queryShape:'name-tokens',term:fixtureName}});
  if(!res.ok())throw Error('Patient search HTTP '+res.status());const result=await res.json();
  if(!result.results.some(r=>r.patientId===linked.patientId))throw Error('Patient search did not find linked synthetic patient');
  console.log('PASS real patient search returns matching Hospital record');
  const check=await page.request.post('http://localhost:3215/api/crm/hospital/connection/check',{headers:{Origin:'http://localhost:3215'},data:{}});if(!check.ok())throw Error('Saved connection reload failed');
  const failure=await page.request.put('http://localhost:3215/api/crm/hospital/connection',{headers:{Origin:'http://localhost:3215'},data:{baseUrl:'http://169.254.169.254',publicUrl:'http://localhost:3210',clientId:'synthetic',clientSecret:'synthetic'}});if(failure.status()!==400)throw Error('Untrusted target allowed');
  const remains=await page.request.post('http://localhost:3215/api/crm/hospital/connection/check',{headers:{Origin:'http://localhost:3215'},data:{}});if(!remains.ok())throw Error('Failed setup replaced valid connection');
  console.log('PASS untrusted destination rejected, previous connection retained');
 } finally {await browser.close();}
})().catch(e=>{console.error(e.message);process.exitCode=1});
