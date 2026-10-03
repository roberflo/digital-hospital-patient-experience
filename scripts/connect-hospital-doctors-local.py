#!/usr/bin/env python3
"""Configure shared doctor login against the existing synthetic local Hospital.
Creates only an OAuth client: no users, roles, service account or clinical grants.
Keeps its secret in the ignored .env. Does not modify Hospital's source tree.
"""
import json, os, pathlib, secrets, urllib.request, urllib.parse, urllib.error
root = pathlib.Path(__file__).resolve().parents[1]
def read_env(path):
    return {k: v.strip().strip('\"').strip("'") for line in path.read_text().splitlines() if line and not line.startswith('#') and '=' in line for k, v in [line.split('=', 1)]}
hospital = read_env(root.parent / 'Hospital' / '.env')
if hospital.get('KC_SEED_DEV_USERS') != 'true':
    raise SystemExit('Only the synthetic local Hospital is supported.')
base = 'http://127.0.0.1:' + hospital.get('KC_PORT_HOST', '8081')
def request(path, method='GET', body=None, token=None, form=False):
    headers = {'Authorization': 'Bearer ' + token} if token else {}
    if body is not None: headers['Content-Type'] = 'application/x-www-form-urlencoded' if form else 'application/json'
    data = None if body is None else (urllib.parse.urlencode(body) if form else json.dumps(body)).encode()
    with urllib.request.urlopen(urllib.request.Request(base + path, data=data, headers=headers, method=method), timeout=20) as res:
        data = res.read()
        return json.loads(data) if data else None

def configure():
    auth = request('/realms/master/protocol/openid-connect/token', 'POST', {'grant_type': 'password', 'client_id': 'admin-cli', 'username': hospital['KC_BOOTSTRAP_ADMIN_USERNAME'], 'password': hospital['KC_BOOTSTRAP_ADMIN_PASSWORD']}, form=True)['access_token']
    realm = '/admin/realms/hospital'
    client_id = 'recepcion-staff-local'
    clients = request(realm + '/clients?clientId=' + client_id, token=auth)
    redirect = 'http://localhost:3215/api/auth/callback/keycloak'
    if not clients:
        request(realm + '/clients', 'POST', {
            'clientId': client_id, 'name': 'Recepción · acceso compartido local', 'enabled': True,
            'protocol': 'openid-connect', 'publicClient': False, 'serviceAccountsEnabled': False,
            'standardFlowEnabled': True, 'directAccessGrantsEnabled': False,
            'secret': secrets.token_urlsafe(48), 'redirectUris': [redirect],
            'webOrigins': ['http://localhost:3215'], 'fullScopeAllowed': True,
            'defaultClientScopes': ['basic', 'profile', 'email', 'roles', 'tenant-context'],
            'attributes': {'pkce.code.challenge.method': 'S256'},
            'protocolMappers': [{'name': 'hospital-audience', 'protocol': 'openid-connect', 'protocolMapper': 'oidc-audience-mapper', 'config': {'included.client.audience': 'hospital-api', 'access.token.claim': 'true', 'id.token.claim': 'false'}}]
        }, auth)
        clients = request(realm + '/clients?clientId=' + client_id, token=auth)
    client = request(realm + '/clients/' + clients[0]['id'], token=auth)
    if client.get('serviceAccountsEnabled') or client.get('directAccessGrantsEnabled') or client.get('publicClient') or client.get('redirectUris') != [redirect]:
        raise SystemExit('Existing client differs from the expected local-only configuration.')
    scopes = request(realm + '/client-scopes', token=auth)
    for scope in scopes:
        if scope['name'] in ['basic', 'profile', 'email', 'roles', 'tenant-context']:
            request(realm + '/clients/' + client['id'] + '/default-client-scopes/' + scope['id'], 'PUT', token=auth)
    secret = request(realm + '/clients/' + client['id'] + '/client-secret', token=auth)['value']
    issuer = request('/realms/hospital/.well-known/openid-configuration')['issuer']
    updates = {'KEYCLOAK_ISSUER': issuer, 'KEYCLOAK_INTERNAL_ISSUER': 'http://hospital-keycloak-1:8080/realms/hospital', 'KEYCLOAK_CLIENT_ID': client_id, 'KEYCLOAK_CLIENT_SECRET': secret, 'Auth__Authority': issuer, 'Auth__Audience': 'hospital-api'}
    path = root / '.env'
    lines = [line for line in path.read_text().splitlines() if line.split('=', 1)[0] not in updates]
    path.write_text('\n'.join(lines + [k + '=' + v for k, v in updates.items()]) + '\n')
    os.chmod(path, 0o600)
    print('Local shared Hospital login configured. Existing roles unchanged; no secrets printed.')
if __name__ == '__main__':
    try: configure()
    except urllib.error.HTTPError as error: raise SystemExit('Local configuration HTTP ' + str(error.code)) from None
