"""Start the local agenda API using an explicit runtime allowlist, never admin secrets.
Requires the approved service account from connect-hospital-local.py and the built
hospital/api:recepcion-patient-agenda image. Keeps the existing Hospital API intact.
"""
import json
import os
import re
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[1]
allowed = {
    'HOSPITAL_PG_CONNECTION', 'HOSPITAL_JOBS_PG_CONNECTION', 'HOSPITAL_KEYRING_PG_CONNECTION',
    'KEYCLOAK__AUTHORITY', 'KEYCLOAK__AUDIENCE', 'KEYCLOAK__METADATAADDRESS',
    'ASPNETCORE_ENVIRONMENT', 'ASPNETCORE_URLS', 'TZ',
    'DATAPROTECTION__KEYRINGSTORE', 'DATAPROTECTION__APPLICATIONNAME',
    'DATAPROTECTION__KEYLIFETIME_DAYS', 'DATAPROTECTION__KEK__MODE',
    'DATAPROTECTION__KEK__CERTIFICATE_PATH', 'DATAPROTECTION__KEK__PASSWORD_FILE',
}
config = json.loads(subprocess.check_output(['docker', 'inspect', 'hospital-api-1']))[0]['Config']
values = dict(value.split('=', 1) for value in config['Env'])
if values.get('ASPNETCORE_ENVIRONMENT') != 'Development':
    raise SystemExit('Only the local development Hospital is supported.')
selected = [key + '=' + values[key] for key in sorted(allowed) if key in values]
delivery = root / 'secrets/hospital-delivery.env'
if delivery.exists():
    for line in delivery.read_text().splitlines():
        if not re.fullmatch(r'ReceptionDelivery__Tenants__[0-9a-f-]{36}__(Enabled|ClientId|ServiceAccountSubject)=[a-zA-Z0-9-]+', line):
            raise SystemExit('Unexpected delivery capability setting.')
        selected.append(line)
if any('\n' in value or '\r' in value for value in selected):
    raise SystemExit('Multiline runtime setting cannot be represented safely.')
path = root / 'secrets/hospital-runtime.env'
path.parent.mkdir(exist_ok=True)
with os.fdopen(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600), 'w') as file:
    os.fchmod(file.fileno(), 0o600)
    file.write('\n'.join(selected) + '\n')
subprocess.run(['docker', 'compose', '-f', 'integrations/hospital/patient-agenda.compose.yml', 'up', '-d'], cwd=root,
               env={**os.environ, 'HOSPITAL_RECEPTION_RUNTIME_ENV_FILE': str(path)}, check=True)
subprocess.run(['docker', 'compose', '-f', 'docker-compose.yml', '-f', 'docker-compose.hospital.yml', 'up', '-d'], cwd=root, check=True)
print('Local agenda connected; no administrator, MinIO or migration credentials copied.')
