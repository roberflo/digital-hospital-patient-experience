"""Integration checks against the local Development stack; synthetic data only.
Run: python3 tests/api_smoke.py. Never point this at production.
"""
import hashlib,hmac,json,pathlib,time,urllib.request,urllib.error,uuid,base64
BASE='http://127.0.0.1:5215'
env=dict(line.split('=',1) for line in pathlib.Path('.env').read_text().splitlines() if '=' in line and not line.startswith('#'))
count=0

def call(path,method='GET',body=None,token=None,headers=None,raw=None):
    h={'Content-Type':'application/json',**(headers or {})}
    if token:h['Authorization']='Bearer '+token
    payload=raw if raw is not None else None if body is None else json.dumps(body).encode()
    req=urllib.request.Request(BASE+path,data=payload,headers=h,method=method)
    try:
        with urllib.request.urlopen(req,timeout=20) as res:
            data=res.read();return res.status,json.loads(data) if data and res.headers.get('Content-Type','').startswith('application/json') else data
    except urllib.error.HTTPError as e:
        data=e.read()
        try:data=json.loads(data)
        except ValueError:pass
        return e.code,data

def check(ok,name):
    global count
    assert ok,name
    count+=1;print('PASS',name)

def login(user):
    status,data=call('/auth/dev','POST',{'user':user,'password':env.get('DEV_PASSWORD','demo-recepcion')})
    assert status==200,(status,data)
    return data['accessToken']

def webhook(payload,event='whatsapp.message.received',key=None,signature=None):
    raw=json.dumps(payload,separators=(',',':')).encode()
    sig=signature if signature is not None else hmac.new(env['KAPSO_WEBHOOK_SECRET'].encode(),raw,hashlib.sha256).hexdigest()
    return call('/webhooks/kapso','POST',raw=raw,headers={'X-Webhook-Signature':sig,'X-Webhook-Event':event,'X-Idempotency-Key':key or str(uuid.uuid4())})

check(call('/health/ready')[0]==200,'PostgreSQL readiness')
check(call('/api/contacts')[0]==401,'Unauthenticated requests rejected')
a=login('admin');b=login('other');agent=login('agent');doctor=login('doctor')
check(call('/api/settings',token=agent)[0]==403,'Agent cannot administer tenant')
check(call('/api/settings',token=doctor)[0]==403,'Doctor cannot administer tenant')
check(call('/api/me',token=a)[1]['tenant']['id']!=call('/api/me',token=b)[1]['tenant']['id'],'Distinct tenants authenticated')
# Signed conflicting membership must still be rejected.
parts=a.split('.');payload=json.loads(base64.urlsafe_b64decode(parts[1]+'=='));payload['tenant_id']='22222222-2222-4222-8222-222222222222'
parts[1]=base64.urlsafe_b64encode(json.dumps(payload).encode()).decode().rstrip('=');parts[2]=base64.urlsafe_b64encode(hmac.new(env['DEV_JWT_KEY'].encode(),'.'.join(parts[:2]).encode(),hashlib.sha256).digest()).decode().rstrip('=')
check(call('/api/contacts',token='.'.join(parts))[0]==403,'Single-tenant user membership enforced despite signed tenant claim')
suffix=str(int(time.time()))[-7:];phone='50371'+suffix
status,c=call('/api/contacts','POST',{'name':'Paciente Sintético '+suffix,'phone':phone,'email':'test@example.invalid','tags':'API test','tenantId':'22222222-2222-4222-8222-222222222222'},a)
check(status==201,'Create contact persists')
check(c['tenantId']=='11111111-1111-4111-8111-111111111111','Mass assignment cannot change tenant')
check(call('/api/contacts','POST',{'name':'Duplicate','phone':phone},a)[0]==409,'Duplicate phone is rejected')
check(not any(x['id']==c['id'] for x in call('/api/contacts',token=b)[1]),'Cross-tenant list isolation')
check(call('/api/contacts/'+c['id'],'PUT',{'name':'Attack','phone':phone},b)[0]==404,'Cross-tenant update rejected')
check(call('/api/activities','POST',{'body':'attack','contactId':c['id']},b)[0]==404,'Cross-tenant note reference rejected')
check(call('/api/opportunities','POST',{'title':'attack','contactId':c['id'],'value':0,'stage':'new'},b)[0]==404,'Cross-tenant opportunity rejected')
status,o=call('/api/opportunities','POST',{'title':'Control sintético','contactId':c['id'],'value':50,'stage':'new'},a)
check(status==200,'Create followup')
check(call('/api/opportunities/'+o['id'],'PATCH',{'stage':'scheduled'},a)[0]==200,'Move opportunity stage')
check(call('/api/opportunities/'+o['id'],'PATCH',{'stage':'bogus'},a)[0]==400,'Invalid stage rejected')
check(call('/api/activities','POST',{'body':'Nota sintética para prueba','contactId':c['id']},a)[0]==200,'Create timeline note')
check(call('/api/hospital/contacts/'+c['id']+'/prescriptions',token=agent)[0]==403,'Internal clinical content restricted to doctor')

wid='wamid.test.'+suffix
p={'phone_number_id':'demo','message':{'id':wid,'timestamp':str(int(time.time())),'from':phone,'type':'text','text':{'body':'Consulta sintética de integración'},'kapso':{'direction':'inbound','status':'received','origin':'cloud_api','content':'Consulta sintética de integración'}},'conversation':{'id':'conv-'+suffix,'phone_number':phone,'contact_name':'Paciente Sintético '+suffix}}
check(webhook(p,signature='invalid')[0]==401,'Malformed webhook signature rejected')
check(webhook(p,signature='00'*32)[0]==401,'Forged webhook signature rejected')
check(webhook(p,key='receipt-'+suffix)[0]==200,'Valid signed webhook accepted')
check(webhook(p,key='receipt-'+suffix)[0]==200,'Webhook delivery retry acknowledged')
check(webhook(p,key='another-'+suffix)[0]==200,'Same message with new delivery key acknowledged')
chats=call('/api/conversations',token=a)[1];chat=next(x for x in chats if x['contact']['id']==c['id']);cid=chat['conversation']['id']
msgs=call('/api/conversations/'+cid+'/messages',token=a)[1]
check(sum(x['externalId']==wid for x in msgs)==1,'Message deduplication across delivery keys')
check(call('/api/conversations/'+cid+'/messages',token=b)[0]==404,'Cross-tenant conversation read rejected')
check(call('/api/conversations/'+cid,'PATCH',{'status':'human','assignedTo':'dev-other'},a)[0]==400,'Cross-tenant assignee rejected')
check(call('/api/conversations/'+cid,'PATCH',{'status':'human','assignedTo':'dev-agent'},agent)[0]==200,'Human takes conversation')
check(call('/api/conversations/'+cid,'PATCH',{'status':'agent','assignedTo':None},a)[0]==200,'Resume agent explicitly')
# Coexistence outbound echoes must pause the bot and be present in timeline.
echo=json.loads(json.dumps(p));echo['message']['id']='wamid.echo.'+suffix;echo['message']['kapso'].update(direction='outbound',origin='business_app',status='sent');echo['message']['text']['body']='Respuesta humana sintética'
check(webhook(echo,'whatsapp.message.sent')[0]==200,'Coexistence business-app echo accepted')
chat=next(x for x in call('/api/conversations',token=a)[1] if x['conversation']['id']==cid)
check(chat['conversation']['status']=='human','Coexistence pauses agent')
# Delivery/read are separate events for the same message.
echo['message']['kapso']['status']='read';check(webhook(echo,'whatsapp.message.read')[0]==200,'Read receipt accepted')
msgs=call('/api/conversations/'+cid+'/messages',token=a)[1];check(next(x for x in msgs if x['externalId']=='wamid.echo.'+suffix)['status']=='read','Read receipt updates existing message')
check(call('/api/conversations/'+cid+'/messages','POST',{'body':'No enviar'},a,headers={'Idempotency-Key':str(uuid.uuid4())})[0]==400,'Disabled channel never sends real messages')
check(call('/api/audit',token=agent)[0]==403,'Audit limited to supervisors')
check(any(x['action']=='contact.created' for x in call('/api/audit',token=a)[1]),'Audit records persisted')
check(call('/api/google/connect','POST',{},agent)[0]==403,'Google connection limited to administrators')
# Inbox workflow and CRM share a tenant-scoped patient and conversation.
from datetime import datetime,timezone,timedelta
base='/api/conversations/'+cid
current=call('/api/conversations?contactId='+c['id'],token=a)[1][0]
check(current['unreadCount']==1,'Inbound message starts unread')
check(call(base+'/read','POST',{'throughMessageId':str(uuid.uuid4())},a)[0]==404,'Unknown read marker rejected')
check(call(base+'/read','POST',{'throughMessageId':msgs[0]['id']},a)[0]==200,'Mark conversation read for current user')
check(call('/api/conversations?contactId='+c['id'],token=a)[1][0]['unreadCount']==0,'Read count cleared')
check(call('/api/conversations?contactId='+c['id'],token=agent)[1][0]['unreadCount']==1,'Read state isolated per user')
revision=current['conversation']['revision']
status,workflow=call(base+'/workflow','PATCH',{'state':'pending','priority':'urgent','labels':'Cita, cita, receta','expectedRevision':revision},a)
check(status==200 and workflow['state']=='pending' and workflow['status']=='human','Pending pauses automation')
check(workflow['labels']=='cita, receta','Conversation labels normalized')
assigned=call(base,'PATCH',{'status':'human','assignedTo':'dev-agent'},a)
check(assigned[0]==200 and assigned[1]['state']=='pending','Assigning a human preserves pending workflow state')
check(call(base+'/workflow','PATCH',{'priority':'low','expectedRevision':revision},a)[0]==409,'Stale conversation update rejected')
check(call(base+'/workflow','PATCH',{'state':'resolved'},b)[0]==404,'Workflow isolated by tenant')
check(any(x['conversation']['id']==cid for x in call('/api/conversations?state=pending&priority=urgent&label=cita',token=a)[1]),'Inbox combines state priority and label filters')
check(not any(x['conversation']['id']==cid for x in call('/api/conversations?state=resolved',token=a)[1]),'State filter excludes other states')
check(call(base+'/workflow','PATCH',{'state':'snoozed'},a)[0]==400,'Snooze requires a future time')
check(call(base+'/workflow','PATCH',{'state':'snoozed','snoozedUntil':(datetime.now(timezone.utc)+timedelta(hours=1)).isoformat()},a)[0]==200,'Conversation can be snoozed')
p['message']['id']='wamid.reopen.'+suffix
check(webhook(p)[0]==200,'New inbound message accepted while snoozed')
reopened=call('/api/conversations?contactId='+c['id'],token=a)[1][0]['conversation']
check(reopened['state']=='open' and reopened['status']=='human' and reopened['snoozedUntil'] is None,'New message reopens for human attention')
check(call('/api/contacts/'+c['id']+'/profile','PATCH',{'name':c['name'],'email':'crm@example.invalid','tags':'Seguimiento','lifecycleStage':'active','companyId':None},a)[0]==200,'Update CRM patient profile from inbox')
status,followup=call('/api/opportunities','POST',{'title':'Seguimiento desde WhatsApp','contactId':c['id'],'conversationId':cid,'value':0,'stage':'new'},a)
check(status==200 and followup['conversationId']==cid,'CRM followup linked to source conversation')
context=call('/api/contacts/'+c['id']+'/context',token=a)[1]
check(context['contact']['lifecycleStage']=='active' and any(x['id']==followup['id'] for x in context['opportunities']) and any(x['kind']=='conversation_state' for x in context['activities']),'Unified CRM context includes profile followups and state history')
check(call('/api/contacts/'+c['id']+'/context',token=b)[0]==404,'Customer context isolated by tenant')
other_contact=next(x for x in call('/api/contacts',token=a)[1] if x['id']!=c['id'])
check(call('/api/opportunities','POST',{'title':'Incorrect relation','contactId':other_contact['id'],'conversationId':cid,'value':0,'stage':'new'},a)[0]==404,'Cannot link followup to another patient conversation')
check(call('/api/activities','POST',{'body':'Incorrect relation','contactId':other_contact['id'],'conversationId':cid},a)[0]==404,'Cannot link note to another patient conversation')
check(call('/api/saved-replies','POST',{'title':'No permitido','body':'Test'},agent)[0]==403,'Only supervisors manage saved replies')
status,reply=call('/api/saved-replies','POST',{'title':'Saludo sintético','body':'Hola, ¿en qué podemos ayudarte?'},a)
check(status==200,'Saved reply created')
check(any(x['id']==reply['id'] for x in call('/api/saved-replies',token=agent)[1]),'Agents can use tenant saved replies')
check(not any(x['id']==reply['id'] for x in call('/api/saved-replies',token=b)[1]),'Saved replies isolated by tenant')
check(call('/api/saved-replies/'+reply['id'],'DELETE',token=a)[0]==200,'Remove synthetic saved reply')
channel_id=chat['channel']['id']
check(call('/api/channels/'+channel_id+'/diagnostics','POST',{},agent)[0]==403,'Channel diagnostics restricted to administrator')
check(call('/api/channels/'+channel_id+'/diagnostics','POST',{},a)[0]==400,'Synthetic channel never claims a live connection')
# Personal views and shared hospital macros.
filters={'state':'pending','assignment':'mine','channelId':channel_id,'priority':'high','label':'Cita','mode':'human'}
status,view=call('/api/inbox-views','POST',{'name':'Mi bandeja sintética','filters':filters},a)
check(status==200,'Personal inbox view persists')
check(any(x['id']==view['id'] for x in call('/api/inbox-views',token=a)[1]),'Owner retrieves saved view')
check(not any(x['id']==view['id'] for x in call('/api/inbox-views',token=agent)[1]),'Saved view is private even within same tenant')
check(call('/api/inbox-views/'+view['id'],'DELETE',token=agent)[0]==404,'Another user cannot delete personal view')
check(call('/api/inbox-views','POST',{'name':'Invalid','filters':dict(filters,assignment='everyone')},a)[0]==400,'Unknown saved filter rejected')
check(call('/api/inbox-views','POST',{'name':'Foreign','filters':filters},b)[0]==404,'Foreign channel cannot be saved')
macro_body={'name':'Revisión sintética','state':'pending','priority':'high','labels':'revision-doctor','note':'Revisión interna sintética','takeOwnership':True}
check(call('/api/macros','POST',macro_body,agent)[0]==403,'Agent cannot author shared macros')
status,macro=call('/api/macros','POST',macro_body,a)
check(status==200,'Supervisor creates shared macro')
check(not any(x['id']==macro['id'] for x in call('/api/macros',token=b)[1]),'Macros isolated by tenant')
current=call('/api/conversations?contactId='+c['id'],token=agent)[1][0]['conversation']
apply_body={'expectedRevision':current['revision']}
message_count=len(call(base+'/messages',token=a)[1])
status,applied=call(base+'/macros/'+macro['id'],'POST',apply_body,agent)
check(status==200 and applied['state']=='pending' and applied['assignedTo']=='dev-agent' and 'revision-doctor' in applied['labels'],'Macro updates workflow labels and assignment together')
check(call(base+'/macros/'+macro['id'],'POST',apply_body,agent)[0]==409,'Duplicate macro request cannot replay against old revision')
check(call(base+'/macros/'+macro['id'],'POST',apply_body,b)[0]==404,'Cross-tenant macro application rejected')
activities=call('/api/activities?conversationId='+cid,token=a)[1]
check(sum(x['kind']=='note' and x['body']=='Revisión interna sintética' for x in activities)==1,'Macro internal note recorded exactly once')
check(len(call(base+'/messages',token=a)[1])==message_count,'Macro never sends a patient message')
check(call('/api/macros/'+macro['id'],'DELETE',token=agent)[0]==403,'Macro removal restricted to supervisors')
check(call('/api/macros/'+macro['id'],'DELETE',token=a)[0]==200,'Remove synthetic macro')
check(call('/api/inbox-views/'+view['id'],'DELETE',token=a)[0]==200,'Owner removes synthetic view')
print('RESULT:',count,'passed, 0 failed')
