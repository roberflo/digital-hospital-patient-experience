#!/usr/bin/env python3
"""Point local Recepción at the local Hospital's Keycloak realm. Creates nothing in Keycloak.

Hospital's own realm job (infra/keycloak/configure-realms.sh §9) declares `recepcion-web` and one
`recepcion-service-<tenant>` service account; this only copies their secrets from Hospital's .env
into Recepción's ignored .env, after checking the service token carries exactly what it should.
It never holds the Keycloak admin password. Synthetic Hospital C only; never a production endpoint.

  --delivery   also enable signed-prescription delivery for Hospital C (explicit operator approval).
               Writes secrets/hospital-delivery.env; Hospital's api compose loads it when
               HOSPITAL_RECEPTION_ENV_FILE in Hospital's .env is set to that absolute path (printed at the end).
"""
import base64,json,os,pathlib,sys,urllib.request,urllib.parse,urllib.error
root=pathlib.Path(__file__).resolve().parents[1]
def read_env(path):
    return {k:v.strip().strip('"').strip("'") for line in path.read_text().splitlines() if line and not line.startswith('#') and '=' in line for k,v in [line.split('=',1)]}
def write_env(path,updates,drop=()):
    lines=[x for x in path.read_text().splitlines() if x.split('=',1)[0] not in updates and x.split('=',1)[0] not in drop] if path.exists() else []
    path.parent.mkdir(exist_ok=True);path.write_text('\n'.join(lines+[k+'='+v for k,v in updates.items()])+'\n');os.chmod(path,0o600)
hospital=read_env(root.parent/'Hospital'/'.env')
if hospital.get('KC_SEED_DEV_USERS')!='true':raise SystemExit('Only the synthetic local Hospital is supported.')
tenant='cccccccc-cccc-4ccc-8ccc-cccccccccccc'
web_secret=hospital.get('KC_RECEPCION_WEB_CLIENT_SECRET');service_secret=hospital.get('KC_RECEPCION_SERVICE_CLIENT_SECRET')
if not web_secret or not service_secret:
    raise SystemExit('Hospital/.env needs KC_RECEPCION_WEB_CLIENT_SECRET and KC_RECEPCION_SERVICE_CLIENT_SECRET; then re-run Hospital\'s keycloak-config job.')
base='http://localhost:'+hospital.get('KC_PORT_HOST','8081')
client_id='recepcion-service-'+tenant
def request(path,body=None):
    data=None if body is None else urllib.parse.urlencode(body).encode()
    with urllib.request.urlopen(urllib.request.Request(base+path,data=data),timeout=20) as res:return json.load(res)

def configure():
    issuer=request('/realms/hospital/.well-known/openid-configuration')['issuer']
    token=request('/realms/hospital/protocol/openid-connect/token',{'grant_type':'client_credentials','client_id':client_id,'client_secret':service_secret})['access_token']
    part=token.split('.')[1];claims=json.loads(base64.urlsafe_b64decode(part+'='*(-len(part)%4)))
    roles=set(claims.get('realm_access',{}).get('roles',[]));aud=claims.get('aud');aud=[aud] if isinstance(aud,str) else aud or []
    if claims.get('tenant_id')!=tenant or roles!={'Recepción','reception-agent'} or 'hospital-api' not in aud:
        raise SystemExit('Service token has unexpected permissions; nothing was saved.')
    internal='http://hospital-keycloak-1:8080/realms/hospital';prefix='Hospital__Tenants__'+tenant+'__'
    updates={'KEYCLOAK_ISSUER':issuer,'KEYCLOAK_INTERNAL_ISSUER':internal,'KEYCLOAK_CLIENT_ID':'recepcion-web','KEYCLOAK_CLIENT_SECRET':web_secret,
        'Auth__Authority':issuer,'Auth__Audience':'hospital-api','DEV_HOSPITAL_TENANT_ID':tenant,
        # Installation-wide: every hospital connects from these, by signing in. Nothing per tenant.
        'HOSPITAL_API_URL':'http://hospital-api-1:8080/','HOSPITAL_SERVICE_CLIENT_SECRET':service_secret,
        'HOSPITAL_ALLOWED_API_ORIGINS':'http://hospital-api-1:8080','HOSPITAL_PUBLIC_URL':'http://localhost:3210'}
    stale=tuple(prefix+k for k in ['BaseUrl','TokenEndpoint','ClientId','ClientSecret','UsePatientAgenda','PublicUrl'])+('ALLOW_DEV_LOGIN','DEV_JWT_KEY','DEV_PASSWORD','HOSPITAL_SELF_ONBOARDING')
    capability_file=root/'secrets/hospital-delivery.env'
    # Delivery stays an explicit opt-in. Once granted, re-runs keep it pointed at the current account.
    if '--delivery' in sys.argv or capability_file.exists():
        updates.update({prefix+'AllowClinicalDelivery':'true',prefix+'UseReceptionBridge':'true'})
        delivery='ReceptionDelivery__Tenants__'+tenant+'__'
        capability={delivery+'Enabled':'true',delivery+'ClientId':client_id,delivery+'ServiceAccountSubject':claims['sub']}
        capability_file.parent.mkdir(exist_ok=True);capability_file.write_text('\n'.join(k+'='+v for k,v in capability.items())+'\n');os.chmod(capability_file,0o600)
    elif (root/'.env').exists() and prefix+'AllowClinicalDelivery' not in read_env(root/'.env'):
        updates[prefix+'AllowClinicalDelivery']='false'
    write_env(root/'.env',updates,drop=stale)
    print('Recepción now signs in through the local Hospital realm (recepcion-web, '+client_id+'); no secrets printed.')
    if capability_file.exists():print('Delivery enabled: set HOSPITAL_RECEPTION_ENV_FILE='+str(capability_file)+' in Hospital\'s .env and recreate hospital-api-1.')

if __name__=='__main__':
    try:configure()
    except urllib.error.HTTPError as error:raise SystemExit('Local configuration failed with HTTP '+str(error.code)) from None
