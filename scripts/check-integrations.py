"""Read-only provider checks. Never prints credentials or patient data."""
import json, pathlib, urllib.request, urllib.error
p=pathlib.Path(__file__).resolve().parents[1]/'.env'
env=dict(line.split('=',1) for line in p.read_text().splitlines() if '=' in line and not line.startswith('#'))
def request(url, headers, data=None):
    payload=None if data is None else json.dumps(data).encode()
    req=urllib.request.Request(url,data=payload,headers=headers)
    with urllib.request.urlopen(req,timeout=40) as r:
        body=r.read().decode()
        if body.startswith('event:') or body.startswith('data:'):
            body=next(line[6:] for line in body.splitlines() if line.startswith('data: '))
        return json.loads(body),r.headers
h={'Authorization':'Bearer '+env['KAPSO_API_KEY'],'Content-Type':'application/json','Accept':'application/json, text/event-stream'}
try:
    data,headers=request('https://api.kapso.ai/mcp',h,{'jsonrpc':'2.0','id':1,'method':'initialize','params':{'protocolVersion':'2024-11-05','capabilities':{},'clientInfo':{'name':'recepcion-dev','version':'1.0'}}})
    if headers.get('Mcp-Session-Id'):h['Mcp-Session-Id']=headers['Mcp-Session-Id']
    print('Kapso MCP initialize:',data.get('result',{}).get('serverInfo',data.get('error')))
    data,_=request('https://api.kapso.ai/mcp',h,{'jsonrpc':'2.0','id':2,'method':'tools/list','params':{}})
    ts=data.get('result',{}).get('tools',[])
    print('Kapso tools:',[t['name'] for t in ts])
    pathlib.Path('artifacts/kapso-tools.json').write_text(json.dumps(ts,indent=2))
except urllib.error.HTTPError as e: print('Kapso MCP HTTP',e.code)
try:
    data,_=request('https://integrate.api.nvidia.com/v1/models',{'Authorization':'Bearer '+env['NVIDIA_API_KEY']})
    models=[m['id'] for m in data.get('data',[])]
    print('NVIDIA models available:',len(models))
    print('Candidate models:',[m for m in models if any(x in m for x in ['llama-3.3-70b','nemotron-3-super','qwen3.5','qwen3-235'])])
except urllib.error.HTTPError as e: print('NVIDIA HTTP',e.code)
