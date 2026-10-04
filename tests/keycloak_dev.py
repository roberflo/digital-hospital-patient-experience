"""Tokens for Hospital's synthetic dev users. Keycloak is Recepción's only issuer.
Local Development only: needs Hospital running with KC_SEED_DEV_USERS=true. Never prints secrets.
"""
import base64,json,os,pathlib,urllib.parse,urllib.request
ROOT=pathlib.Path(__file__).resolve().parents[1]
USERS={'admin':'dev-administrador-a','agent':'dev-recepcion-a','doctor':'dev-medicos-a','other':'dev-administrador-b','hospital':'dev-recepcion-c'}
TENANT_A='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';TENANT_B='bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';TENANT_C='cccccccc-cccc-4ccc-8ccc-cccccccccccc'
def envfile(path):
    return {k:v.strip().strip('"').strip("'") for line in pathlib.Path(path).read_text().splitlines() if line and not line.startswith('#') and '=' in line for k,v in [line.split('=',1)]}
def password():
    return os.environ.get('KC_DEV_USERS_PASSWORD') or envfile(os.environ.get('HOSPITAL_ENV',ROOT.parent/'Hospital'/'.env'))['KC_DEV_USERS_PASSWORD']
def issuer():
    return os.environ.get('KEYCLOAK_ISSUER') or envfile(ROOT/'.env')['Auth__Authority']
def token(user):
    """Access token for a demo alias (admin, agent, doctor, other, hospital) or a raw dev username.
    Uses hospital-web's direct grant, the same one Hospital's own login screen uses."""
    body=urllib.parse.urlencode({'grant_type':'password','client_id':'hospital-web','username':USERS.get(user,user),'password':password(),'scope':'openid'}).encode()
    with urllib.request.urlopen(urllib.request.Request(issuer()+'/protocol/openid-connect/token',data=body),timeout=20) as res:
        return json.load(res)['access_token']
def claims(access_token):
    part=access_token.split('.')[1];return json.loads(base64.urlsafe_b64decode(part+'='*(-len(part)%4)))
def subject(user):
    return claims(token(user))['sub']
