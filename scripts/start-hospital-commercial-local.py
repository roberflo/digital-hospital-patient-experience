"""Start prebuilt commercial Hospital images with existing runtime identities only.
Requires the commercial schema migration; never puts owner/admin DSNs in serving containers.
"""
import json,os,pathlib,subprocess
root=pathlib.Path(__file__).resolve().parents[1]
config=json.loads(subprocess.check_output(['docker','inspect','hospital-web-1']))[0]['Config']
env=dict(x.split('=',1) for x in config['Env'])
allowed={'AUTH_SECRET','AUTH_KEYCLOAK_ISSUER','AUTH_KEYCLOAK_INTERNAL_ISSUER','AUTH_KEYCLOAK_CLIENT_ID','AUTH_KEYCLOAK_SCOPE','AUTH_IDLE_TIMEOUT_SECONDS'}
values={k:v for k,v in env.items() if k in allowed}
values.update({'AUTH_URL':'http://localhost:3210','HOSPITAL_API_BASE_URL':'http://hospital-recepcion-api:8080','NODE_ENV':'production','TZ':'America/El_Salvador'})
assert values.get('AUTH_SECRET') and values.get('AUTH_KEYCLOAK_ISSUER'),'Existing Hospital web authentication required'
path=root/'secrets/hospital-commercial-web.env';path.parent.mkdir(exist_ok=True)
with os.fdopen(os.open(path,os.O_WRONLY|os.O_CREAT|os.O_TRUNC,0o600),'w') as f:
    os.fchmod(f.fileno(),0o600)
    for k,v in values.items():
        if '\n' in v or '\r' in v:raise ValueError('Multiline runtime value')
        f.write(k+'='+v+'\n')
runtime=root/'secrets/hospital-runtime.env';assert runtime.is_file(),'Run start-hospital-local.py first'
subprocess.run(['docker','compose','-f','integrations/hospital/patient-agenda.compose.yml','-f','integrations/hospital/commercial.compose.yml','up','-d','--no-deps','hospital-recepcion-api','hospital-commercial-web'],cwd=root,env={**os.environ,'HOSPITAL_RECEPTION_RUNTIME_ENV_FILE':str(runtime),'HOSPITAL_COMMERCIAL_WEB_ENV_FILE':str(path)},check=True)
print('Commercial Hospital available at http://localhost:3210/es/commercial; original services preserved.')
