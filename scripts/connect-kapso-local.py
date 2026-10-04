"""Operator-only local setup after authorization of tenant, number and public webhook.
Uses Kapso CLI with API-key auth; no user roles change and no messages are sent.
The receiver uses one signing secret, so refuse setup while another number is enabled.
"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import urllib.parse
import uuid

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--tenant', required=True, type=uuid.UUID)
parser.add_argument('--phone-number-id', required=True)
parser.add_argument('--webhook-url', required=True)
args = parser.parse_args()
assert args.phone_number_id.isdigit()
url = urllib.parse.urlsplit(args.webhook_url)
assert url.scheme == 'https' and url.hostname and url.path == '/webhooks/kapso' and not url.query and not url.fragment and not url.username
envfile = root / '.env'
env = dict(line.split('=', 1) for line in envfile.read_text().splitlines() if '=' in line and not line.startswith('#'))
assert env.get('ASPNETCORE_ENVIRONMENT') == 'Development'
cli_env = {**os.environ, 'KAPSO_API_KEY': env['KAPSO_API_KEY'].strip()}

def cli(*command):
    result = subprocess.run(['kapso', *command, '--output', 'json'], env=cli_env, text=True, capture_output=True)
    if result.returncode:
        raise SystemExit('Kapso CLI failed; inspect diagnostics without printing credentials.')
    return json.loads(result.stdout)['data']

def sql(query):
    result = subprocess.run(['docker', 'compose', 'exec', '-T', 'db', 'psql', '-U', 'recepcion', '-d', 'recepcion', '-v', 'ON_ERROR_STOP=1', '-At'], cwd=root, input=query, capture_output=True, text=True)
    if result.returncode:
        raise SystemExit('Local binding refused: ' + result.stderr)
    return result.stdout.strip()

number = cli('whatsapp', 'numbers', 'get', '--phone-number-id', args.phone_number_id)
assert number['id'] == args.phone_number_id and number['status'] == 'CONNECTED' and number['kind'] != 'sandbox'
customer = str(uuid.UUID(number['customer_id']))
tenant = str(args.tenant)
coexistence = 'true' if number.get('is_coexistence') is True else 'false'
assert sql(f'''SELECT count(*) FROM "Channels" WHERE "Enabled" AND "PhoneNumberId" <> '{args.phone_number_id}';''') == '0', 'Another enabled channel prevents replacing the global signing secret.'
# UUIDs and the numeric phone id above are parsed before interpolation. No names or user text enter SQL.
sql(f'''BEGIN;
LOCK TABLE "Channels", "Tenants" IN SHARE ROW EXCLUSIVE MODE;
DO $$ BEGIN
 IF NOT EXISTS(SELECT 1 FROM "Tenants" WHERE "Id"='{tenant}') THEN RAISE EXCEPTION 'Unknown tenant'; END IF;
 IF EXISTS(SELECT 1 FROM "Tenants" WHERE "Id"='{tenant}' AND "KapsoCustomerId" IS NOT NULL AND "KapsoCustomerId"<>'{customer}') THEN RAISE EXCEPTION 'Tenant already bound to another customer'; END IF;
 IF EXISTS(SELECT 1 FROM "Channels" WHERE "PhoneNumberId"='{args.phone_number_id}' AND "TenantId"<>'{tenant}') THEN RAISE EXCEPTION 'Number belongs to another tenant'; END IF;
END $$;
UPDATE "Tenants" SET "KapsoCustomerId"='{customer}' WHERE "Id"='{tenant}';
INSERT INTO "Channels" ("Id","TenantId","Name","PhoneNumberId","Coexistence","Enabled","KapsoCustomerId")
VALUES ('{uuid.uuid4()}','{tenant}','WhatsApp · recepción general','{args.phone_number_id}',{coexistence},false,'{customer}')
ON CONFLICT ("PhoneNumberId") DO NOTHING;
INSERT INTO "Audits" ("Id","TenantId","Actor","Action","Resource","CreatedAt")
VALUES ('{uuid.uuid4()}','{tenant}','local-operator','kapso.number.bound','{args.phone_number_id}',now());
COMMIT;''')
hooks = cli('whatsapp', 'webhooks', 'list', '--phone-number-id', args.phone_number_id)
hook = next((h for h in hooks if h['url'] == args.webhook_url and h['kind'] == 'kapso'), None)
if hook is None:
    events = ['whatsapp.message.received', 'whatsapp.message.sent', 'whatsapp.message.delivered', 'whatsapp.message.read', 'whatsapp.message.failed', 'whatsapp.conversation.created', 'whatsapp.conversation.ended', 'whatsapp.thread.standby']
    flags = [arg for event in events for arg in ['--event', event]]
    hook = cli('whatsapp', 'webhooks', 'new', '--phone-number-id', args.phone_number_id, '--url', args.webhook_url, '--kind', 'kapso', '--payload-version', 'v2', '--inactive', *flags)
    # Capture one-time credentials privately immediately; never emit the raw CLI response.
    directory = root / 'secrets'
    directory.mkdir(exist_ok=True)
    with os.fdopen(os.open(directory / ('kapso-webhook-' + str(uuid.UUID(hook['id'])) + '.json'), os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), 'w') as file:
        json.dump(hook, file)
secret = hook.get('secret_key')
if not secret:
    private = root / 'secrets' / ('kapso-webhook-' + str(uuid.UUID(hook['id'])) + '.json')
    secret = json.loads(private.read_text())['secret_key']
assert secret and '\n' not in secret and '\r' not in secret
updates = {'KAPSO_PHONE_NUMBER_ID': args.phone_number_id, 'KAPSO_WEBHOOK_URL': args.webhook_url, 'KAPSO_WEBHOOK_SECRET': secret, 'SEND_ENABLED': 'false', 'KAPSO_MANUAL_SEND_ENABLED': 'false', 'Kapso__Tenants__' + tenant + '__CustomerId': customer}
lines = [line for line in envfile.read_text().splitlines() if line.split('=', 1)[0] not in updates]
with os.fdopen(os.open(envfile, os.O_WRONLY | os.O_TRUNC), 'w') as file:
    os.fchmod(file.fileno(), 0o600)
    file.write('\n'.join(lines + [k + '=' + v for k, v in updates.items()]) + '\n')
subprocess.run(['docker', 'compose', '-f', 'docker-compose.yml', '-f', 'docker-compose.hospital.yml', '-f', 'docker-compose.kapso-local.yml', 'up', '-d', '--no-deps', 'recepcion-api'], cwd=root, check=True)
sql(f'''BEGIN; UPDATE "Channels" SET "Enabled"=false WHERE "PhoneNumberId"='{args.phone_number_id}' AND "TenantId"='{tenant}';
INSERT INTO "Audits" ("Id","TenantId","Actor","Action","Resource","CreatedAt") VALUES ('{uuid.uuid4()}','{tenant}','local-operator','channel.receive_only','{args.phone_number_id}',now()); COMMIT;''')
cli('whatsapp', 'webhooks', 'update', hook['id'], '--phone-number-id', args.phone_number_id, '--active')
print(json.dumps({'phoneNumberId': args.phone_number_id, 'tenantId': tenant, 'webhookId': hook['id'], 'coexistence': number.get('is_coexistence'), 'webhookUrl': args.webhook_url, 'automatedReplies': 'unchanged; tenant controls apply'}))
