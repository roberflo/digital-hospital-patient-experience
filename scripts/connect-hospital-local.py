#!/usr/bin/env python3
"""Explicit operator approval required: provisions a local Reception-only service client.
Uses the existing synthetic Hospital C. Never targets a production endpoint.
Run from Recepcion after approving this specific persistent access.
"""
import base64,json,os,pathlib,secrets,urllib.request,urllib.parse,urllib.error
root=pathlib.Path(__file__).resolve().parents[1]
def read_env(path):
    return {k:v.strip().strip('"').strip("'") for line in path.read_text().splitlines() if line and not line.startswith('#') and '=' in line for k,v in [line.split('=',1)]}
hospital=read_env(root.parent/'Hospital'/'.env')
if hospital.get('KC_SEED_DEV_USERS')!='true':raise SystemExit('This requires the existing synthetic development Hospital.')
base='http://127.0.0.1:'+hospital.get('KC_PORT_HOST','8081')
tenant='cccccccc-cccc-4ccc-8ccc-cccccccccccc'
client_id='recepcion-agenda-local-c'
def request(path,method='GET',body=None,token=None,form=False):
    headers={}
    if token:headers['Authorization']='Bearer '+token
    if body is not None:headers['Content-Type']='application/x-www-form-urlencoded' if form else 'application/json'
    data=None if body is None else (urllib.parse.urlencode(body) if form else json.dumps(body)).encode()
    with urllib.request.urlopen(urllib.request.Request(base+path,data=data,headers=headers,method=method),timeout=20) as res:
        data=res.read();return json.loads(data) if data else None

def configure():
    auth=request('/realms/master/protocol/openid-connect/token','POST',{'grant_type':'password','client_id':'admin-cli','username':hospital['KC_BOOTSTRAP_ADMIN_USERNAME'],'password':hospital['KC_BOOTSTRAP_ADMIN_PASSWORD']},form=True)['access_token']
    realm='/admin/realms/hospital'
    clients=request(realm+'/clients?clientId='+client_id,token=auth)
    if not clients:
        request(realm+'/clients','POST',{'clientId':client_id,'name':'Recepción agenda · desarrollo sintético C','enabled':True,'protocol':'openid-connect','publicClient':False,'serviceAccountsEnabled':True,'standardFlowEnabled':False,'directAccessGrantsEnabled':False,'fullScopeAllowed':False,'secret':secrets.token_urlsafe(48),'defaultClientScopes':['roles'],'protocolMappers':[
            {'name':'hospital-audience','protocol':'openid-connect','protocolMapper':'oidc-audience-mapper','config':{'included.client.audience':'hospital-api','access.token.claim':'true','id.token.claim':'false'}},
            {'name':'hospital-tenant','protocol':'openid-connect','protocolMapper':'oidc-hardcoded-claim-mapper','config':{'claim.name':'tenant_id','claim.value':tenant,'jsonType.label':'String','access.token.claim':'true','id.token.claim':'false'}}
        ]},auth)
        clients=request(realm+'/clients?clientId='+client_id,token=auth)
    client=clients[0]
    if client.get('fullScopeAllowed') or client.get('publicClient') or not client.get('serviceAccountsEnabled'):
        raise SystemExit('Existing client has unexpected configuration; stop for review.')
    uid=client['id'];role=request(realm+'/roles/'+urllib.parse.quote('Recepción'),token=auth)
    account=request(realm+'/clients/'+uid+'/service-account-user',token=auth)
    request(realm+'/users/'+account['id']+'/role-mappings/realm','POST',[role],auth)
    request(realm+'/clients/'+uid+'/scope-mappings/realm','POST',[role],auth)
    secret=request(realm+'/clients/'+uid+'/client-secret',token=auth)['value']
    token=request('/realms/hospital/protocol/openid-connect/token','POST',{'grant_type':'client_credentials','client_id':client_id,'client_secret':secret},form=True)['access_token']
    claims=json.loads(base64.urlsafe_b64decode(token.split('.')[1]+'=='))
    roles=set(claims.get('realm_access',{}).get('roles',[]))
    if claims.get('tenant_id')!=tenant or 'Recepción' not in roles or roles & {'Administrador','Médicos','Odontólogos','Nutricionistas'} or 'hospital-api' not in claims.get('aud',[]):
        raise SystemExit('Resulting token has unexpected permissions; configuration was not saved.')
    prefix='Hospital__Tenants__'+tenant+'__'
    updates={prefix+'BaseUrl':'http://hospital-recepcion-api:8080/',prefix+'TokenEndpoint':'http://hospital-keycloak-1:8080/realms/hospital/protocol/openid-connect/token',prefix+'ClientId':client_id,prefix+'ClientSecret':secret,prefix+'AllowClinicalDelivery':'false',prefix+'UsePatientAgenda':'true','DEV_HOSPITAL_TENANT_ID':tenant}
    path=root/'.env';lines=[x for x in path.read_text().splitlines() if x.split('=',1)[0] not in updates]
    path.write_text('\n'.join(lines+[k+'='+v for k,v in updates.items()])+'\n');os.chmod(path,0o600)
    print('Configured dedicated Reception-only client for local synthetic Hospital C; no secrets printed.')

if __name__=='__main__':
    try:configure()
    except urllib.error.HTTPError as error:raise SystemExit('Local configuration failed with HTTP '+str(error.code)) from None
