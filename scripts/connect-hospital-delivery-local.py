#!/usr/bin/env python3
"""Configure the user-authorized prescription delivery capability only for synthetic Hospital C.
No clinician role, public endpoint, message send or prescription mutation is granted.
"""
import base64
import importlib.util
import json
import os
from pathlib import Path
import secrets
import urllib.error
import urllib.parse

root=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('local_agenda',root/'scripts/connect-hospital-local.py')
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
request=module.request;hospital=module.hospital;tenant=module.tenant
client_id='recepcion-agent-local-c'

def configure():
    auth=request('/realms/master/protocol/openid-connect/token','POST',{'grant_type':'password','client_id':'admin-cli','username':hospital['KC_BOOTSTRAP_ADMIN_USERNAME'],'password':hospital['KC_BOOTSTRAP_ADMIN_PASSWORD']},form=True)['access_token']
    realm='/admin/realms/hospital'
    clients=request(realm+'/clients?clientId='+client_id,token=auth)
    if not clients:
        request(realm+'/clients','POST',{'clientId':client_id,'name':'Recepción agente · clínica sintética C','enabled':True,'protocol':'openid-connect','publicClient':False,'serviceAccountsEnabled':True,'standardFlowEnabled':False,'directAccessGrantsEnabled':False,'fullScopeAllowed':False,'secret':secrets.token_urlsafe(48),'defaultClientScopes':['roles'],'protocolMappers':[
            {'name':'hospital-audience','protocol':'openid-connect','protocolMapper':'oidc-audience-mapper','config':{'included.client.audience':'hospital-api','access.token.claim':'true','id.token.claim':'false'}},
            {'name':'hospital-tenant','protocol':'openid-connect','protocolMapper':'oidc-hardcoded-claim-mapper','config':{'claim.name':'tenant_id','claim.value':tenant,'jsonType.label':'String','access.token.claim':'true','id.token.claim':'false'}}]},auth)
        clients=request(realm+'/clients?clientId='+client_id,token=auth)
    client=clients[0];assert not client.get('fullScopeAllowed') and not client.get('publicClient') and client.get('serviceAccountsEnabled')
    uid=client['id'];account=request(realm+'/clients/'+uid+'/service-account-user',token=auth)
    for name in ['Recepción','reception-agent']:
        try: role=request(realm+'/roles/'+urllib.parse.quote(name),token=auth)
        except urllib.error.HTTPError as error:
            if error.code!=404 or name!='reception-agent': raise
            request(realm+'/roles','POST',{'name':name,'description':'Dedicated phone-bound prescription delivery capability'},auth)
            role=request(realm+'/roles/'+name,token=auth)
        request(realm+'/users/'+account['id']+'/role-mappings/realm','POST',[role],auth)
        request(realm+'/clients/'+uid+'/scope-mappings/realm','POST',[role],auth)
    secret=request(realm+'/clients/'+uid+'/client-secret',token=auth)['value']
    token=request('/realms/hospital/protocol/openid-connect/token','POST',{'grant_type':'client_credentials','client_id':client_id,'client_secret':secret},form=True)['access_token']
    claims=json.loads(base64.urlsafe_b64decode(token.split('.')[1]+'=='));roles=set(claims.get('realm_access',{}).get('roles',[]))
    assert claims['tenant_id']==tenant and claims['sub']==account['id'] and {'Recepción','reception-agent'}<=roles
    assert not roles & {'Administrador','Médicos','Odontólogos','Nutricionistas'}
    prefix='Hospital__Tenants__'+tenant+'__'
    updates={prefix+'ClientId':client_id,prefix+'ClientSecret':secret,prefix+'AllowClinicalDelivery':'true',prefix+'UseReceptionBridge':'true'}
    path=root/'.env';lines=[x for x in path.read_text().splitlines() if x.split('=',1)[0] not in updates]
    path.write_text('\n'.join(lines+[k+'='+v for k,v in updates.items()])+'\n');os.chmod(path,0o600)
    prefix='ReceptionDelivery__Tenants__'+tenant+'__'
    capability={prefix+'Enabled':'true',prefix+'ClientId':client_id,prefix+'ServiceAccountSubject':account['id']}
    path=root/'secrets/hospital-delivery.env';path.write_text('\n'.join(k+'='+v for k,v in capability.items())+'\n');os.chmod(path,0o600)
    runtime=root/'secrets/hospital-runtime.env';lines=[x for x in runtime.read_text().splitlines() if x.split('=',1)[0] not in capability]
    runtime.write_text('\n'.join(lines+[k+'='+v for k,v in capability.items()])+'\n');os.chmod(runtime,0o600)
    print('Synthetic Hospital C delivery capability configured; no clinician grants, secrets or messages emitted.')

if __name__=='__main__':
    try:configure()
    except urllib.error.HTTPError as error:raise SystemExit('Local capability configuration failed: HTTP '+str(error.code)) from None
