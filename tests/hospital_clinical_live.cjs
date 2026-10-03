// Opt-in: actual local Hospital C and isolated browser. No WhatsApp sends or clinical writes.
// Temporary disabled-channel fixtures are removed in finally; clinical access audit is retained.
const { chromium } = require('../frontend/node_modules/playwright');
const { readFileSync } = require('node:fs');
const { execFileSync } = require('node:child_process');
const { randomUUID } = require('node:crypto');
const root = require('node:path').resolve(__dirname, '..');
const env = Object.fromEntries(readFileSync(root + '/../Hospital/.env', 'utf8').split('\n').filter(l => l && !l.startsWith('#') && l.includes('=')).map(l => {const i=l.indexOf('=');return [l.slice(0,i),l.slice(i+1).trim().replace(/^["']|["']$/g,'')];}));
if(env.KC_SEED_DEV_USERS !== 'true') throw new Error('Synthetic Hospital required');
const tenant='cccccccc-cccc-4ccc-8ccc-cccccccccccc', patient='01a0a362-0dd9-7418-a987-ad9c8e67758b';
const channel=randomUUID(), conversation=randomUUID();
function sql(text){return execFileSync('docker',['compose','exec','-T','db','psql','-U','recepcion','-d','recepcion','-v','ON_ERROR_STOP=1','-q'],{cwd:root,input:text,encoding:'utf8'});}
(async()=>{
 const browser=await chromium.launch({headless:true});const context=await browser.newContext();const page=await context.newPage();let seeded=false;
 try {
  await page.goto('http://localhost:3215/login');
  await page.getByRole('button',{name:'Continuar con mi cuenta del hospital'}).click();
  await page.locator('#username').fill('dev-medicos-c');await page.locator('#password').fill(env.KC_DEV_USERS_PASSWORD);await page.locator('#kc-login').click();
  await page.waitForURL('http://localhost:3215/',{timeout:30000});
  let response=await page.request.get('http://localhost:3215/api/crm/me');if(response.status()!==200)throw new Error('Shared login HTTP '+response.status());
  const me=await response.json();if(me.role!=='doctor'||me.tenant.id!==tenant)throw new Error('Identity mismatch');
  console.log('PASS real Keycloak doctor login and CRM tenant binding');
  const contacts=await (await page.request.get('http://localhost:3215/api/crm/contacts')).json();const contact=contacts.find(c=>c.patientId===patient);if(!contact)throw new Error('Run hospital_live.py first to link the existing synthetic patient');
  if(!/^[0-9a-f-]{36}$/.test(contact.id))throw new Error('Invalid fixture ID');
  sql(`BEGIN;
INSERT INTO "Channels" ("Id","TenantId","Name","PhoneNumberId","Coexistence","Enabled") VALUES ('${channel}','${tenant}','Prueba clínica sintética','clinical-test-${channel}',false,false);
INSERT INTO "Conversations" ("Id","TenantId","ContactId","ChannelId","ExternalId","Status","Summary","UpdatedAt","State","Priority","Labels","Revision") SELECT '${conversation}','${tenant}','${contact.id}','${channel}','','human',"Summary",now(),'open','normal','',0 FROM "Conversations" WHERE "TenantId"='11111111-1111-4111-8111-111111111111' LIMIT 1;
COMMIT;`);seeded=true;
  const base=`http://localhost:3215/api/crm/hospital/conversations/${conversation}/clinical`;
  if((await page.request.get(base+'/timeline')).status()!==403)throw new Error('Unassigned access allowed');
  const take=await page.request.patch(`http://localhost:3215/api/crm/conversations/${conversation}`,{headers:{Origin:'http://localhost:3215'},data:{status:'human',assignedTo:me.subject,expectedRevision:0}});if(take.status()!==200)throw new Error('Take conversation HTTP '+take.status());
  let timeline;
  for(const section of ['timeline','antecedentes','allergies']){
    const r=await page.request.get(base+'/'+section);if(r.status()!==200)throw new Error(section+' HTTP '+r.status()+' '+(await r.json()).title);
    const value=await r.json();if(section==='timeline')timeline=value;
    console.log('PASS live Hospital '+section+' through Reception BFF');
  }
  const found={prescription:0,note:0};
  for(const entry of timeline.items){
    if(!entry.sourceRef || !['prescription','note'].includes(entry.entryType)||entry.state==='draft'||entry.summaryKey==='entered-in-error')continue;
    const r=await page.request.get(base+'/'+(entry.entryType==='prescription'?'prescriptions':'notes')+'/'+entry.sourceRef.id);
    if(r.status()!==200)throw new Error('Document HTTP '+r.status());found[entry.entryType]++;
  }
  console.log('PASS live document reads: '+JSON.stringify(found));
  await page.goto(`http://localhost:3215/?view=inbox&conversation=${conversation}`);
  // Use the exact fixture's card, without touching the user's open browser or conversations.
  await page.locator('.conversation-card').filter({hasText:contact.name}).first().click();
  await page.getByRole('button',{name:'Expediente y recetas',exact:true}).click();
  await page.getByRole('dialog').getByText('Historial de Hospital',{exact:false}).waitFor();
  await page.getByRole('button',{name:'Ver receta',exact:true}).first().click();
  await page.getByRole('dialog').getByText('Dosis registrada:',{exact:false}).first().waitFor();
  await page.screenshot({path:root+'/artifacts/clinical-hospital-live.png',fullPage:true});
  console.log('PASS actual Hospital clinical reader in browser');
 } finally {
  if(seeded)sql(`DELETE FROM "Activities" WHERE "ConversationId"='${conversation}'; DELETE FROM "Conversations" WHERE "Id"='${conversation}'; DELETE FROM "Channels" WHERE "Id"='${channel}';`);
  await browser.close();
 }
})().catch(e=>{console.error(e.message);process.exitCode=1;});
