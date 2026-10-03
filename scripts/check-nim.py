"""Synthetic function-calling smoke test. No patient or CRM data sent."""
import json,pathlib,urllib.request
root=pathlib.Path(__file__).resolve().parents[1]
e=dict(line.split('=',1) for line in (root/'.env').read_text().splitlines() if '=' in line and not line.startswith('#'))
payload={'model':e['AI_MODEL'],'messages':[{'role':'user','content':'Prueba técnica sintética: consulta disponibilidad para el 2026-10-05 usando la herramienta. No hay un paciente real.'}],'tools':[{'type':'function','function':{'name':'hospital_availability','description':'Obtener disponibilidad ficticia','parameters':{'type':'object','properties':{'date':{'type':'string'}},'required':['date']}}}],'tool_choice':'auto','max_tokens':1000,'temperature':0.1,'stream':False,'chat_template_kwargs':{'enable_thinking':False}}
req=urllib.request.Request('https://integrate.api.nvidia.com/v1/chat/completions',headers={'Authorization':'Bearer '+e['NVIDIA_API_KEY'],'Content-Type':'application/json'},data=json.dumps(payload).encode())
with urllib.request.urlopen(req,timeout=90) as r:res=json.load(r)
message=res['choices'][0]['message'];calls=message.get('tool_calls',[])
assert calls and calls[0]['function']['name']=='hospital_availability','Expected tool call'
args=json.loads(calls[0]['function']['arguments']);assert args['date']=='2026-10-05'
print('PASS NVIDIA NIM authenticated completion + function calling:',res.get('model'),'tokens:',res.get('usage',{}).get('total_tokens'))
