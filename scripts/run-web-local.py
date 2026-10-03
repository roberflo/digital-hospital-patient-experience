import os,pathlib
root=pathlib.Path(__file__).resolve().parents[1]
env=os.environ.copy()
for line in (root/'.env').read_text().splitlines():
    if '=' in line and not line.startswith('#'):
        k,v=line.split('=',1)
        if k in ['AUTH_SECRET','ALLOW_DEV_LOGIN','ASPNETCORE_ENVIRONMENT','NEXTAUTH_URL','KEYCLOAK_CLIENT_ID','KEYCLOAK_CLIENT_SECRET','KEYCLOAK_ISSUER']:env[k]=v
env['API_URL']='http://127.0.0.1:5215';env['NEXTAUTH_SECRET']=env['AUTH_SECRET'];env['NEXT_TELEMETRY_DISABLED']='1'
os.chdir(root/'frontend');os.execvpe('npm',['npm','run','dev'],env)
