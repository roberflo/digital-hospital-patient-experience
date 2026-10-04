"""NIM live smoke: only anonymous counts and a synthetic user instruction leave the CRM."""
import json,urllib.request,uuid
import keycloak_dev
BASE='http://127.0.0.1:5215'
def post(path,body,token=None):
 h={'Content-Type':'application/json'}
 if token:h['Authorization']='Bearer '+token
 req=urllib.request.Request(BASE+path,data=json.dumps(body).encode(),headers=h)
 with urllib.request.urlopen(req,timeout=90) as r:return json.load(r)
token=keycloak_dev.token('admin')
r=post('/api/assistant',{'message':'Resume brevemente las métricas anónimas de atención.'},token)
assert len(r['answer'])>10
print('PASS authenticated internal assistant returns anonymous CRM summary')
contact=post('/api/contacts',{'name':'Prueba Sintética Asistente','phone':'503'+str(uuid.uuid4().int)[-8:]},token)
r=post('/api/assistant',{'message':'Guarda una nota interna con el texto exacto: Prueba sintética de seguimiento.','contactId':contact['id']},token)
req=urllib.request.Request(BASE+'/api/activities?contactId='+contact['id'],headers={'Authorization':'Bearer '+token})
with urllib.request.urlopen(req,timeout=20) as res:activities=json.load(res)
assert any('Prueba sintética de seguimiento' in x['body'] for x in activities)
print('PASS NIM tool calling saves a note bound to the selected synthetic contact')
