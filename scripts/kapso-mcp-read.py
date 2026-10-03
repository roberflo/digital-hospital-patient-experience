import json,pathlib,urllib.request,sys
root=pathlib.Path(__file__).resolve().parents[1]
e=dict(line.split('=',1) for line in (root/'.env').read_text().splitlines() if '=' in line and not line.startswith('#'))
h={'Authorization':'Bearer '+e['KAPSO_API_KEY'],'Content-Type':'application/json','Accept':'application/json, text/event-stream'}
def rpc(mid,method,params):
 req=urllib.request.Request('https://api.kapso.ai/mcp',headers=h,data=json.dumps({'jsonrpc':'2.0','id':mid,'method':method,'params':params}).encode())
 with urllib.request.urlopen(req,timeout=40) as r:
  if r.headers.get('Mcp-Session-Id'):h['Mcp-Session-Id']=r.headers['Mcp-Session-Id']
  body=r.read().decode()
  if not body.startswith('{'):body=next(s[6:] for s in body.splitlines() if s.startswith('data: '))
  return json.loads(body)
rpc(1,'initialize',{'protocolVersion':'2024-11-05','capabilities':{},'clientInfo':{'name':'recepcion-dev','version':'1.0'}})
args={'action':'get','params':{'phone_number_id':e['KAPSO_PHONE_NUMBER_ID']}}
res=rpc(2,'tools/call',{'name':'whatsapp_numbers','arguments':args})
(root/'artifacts/kapso-number.json').write_text(json.dumps(res,indent=2))
print(json.dumps(res)[:7000])
