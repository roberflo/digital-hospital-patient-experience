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
print('RESULT:',count,'passed, 0 failed')
