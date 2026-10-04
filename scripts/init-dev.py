"""Create a local synthetic demo environment; never overwrites an existing file."""
from pathlib import Path
import secrets
root=Path(__file__).resolve().parents[1]
p=root/'.env'
if p.exists():raise SystemExit('.env already exists; preserved.')
values={k:v for line in (root/'.env.example').read_text().splitlines() if line and not line.startswith('#') for k,v in [line.split('=',1)]}
values.update({k:secrets.token_urlsafe(48) for k in ['POSTGRES_PASSWORD','PHONE_HASH_KEY','AUTH_SECRET','KAPSO_WEBHOOK_SECRET']})
values.update(ASPNETCORE_ENVIRONMENT='Development',INITIALIZE_DATABASE='true',NEXTAUTH_URL='http://localhost:3215',FRONTEND_URL='http://localhost:3215',BOOTSTRAP_TENANT_ID='',BOOTSTRAP_TENANT_NAME='')
p.write_text('\n'.join(k+'='+v for k,v in values.items())+'\n');p.chmod(0o600)
print('Created .env for local Development. Sign-in is Hospital\'s Keycloak: run scripts/connect-hospital-local.py next.')
