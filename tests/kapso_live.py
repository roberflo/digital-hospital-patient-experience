"""Opt-in local integration check; no outbound WhatsApp messages.
Requires the configured local hospital fixture, .env, and authorized webhook tunnel.
Checks tenant isolation and public signed webhook -> persistent Node SSE.
"""
import hashlib
import hmac
import html
import http.cookiejar
import json
import re
from pathlib import Path
import urllib.error
import urllib.parse
import urllib.request
import uuid

import keycloak_dev

root = Path(__file__).resolve().parents[1]
env = dict(line.split('=', 1) for line in (root / '.env').read_text().splitlines() if '=' in line and not line.startswith('#'))
assert env.get('ASPNETCORE_ENVIRONMENT') == 'Development', 'Local development fixture only'
base = 'http://localhost:3215'

class LocalhostCookies(http.cookiejar.DefaultCookiePolicy):
    # Browsers treat http://localhost as a secure context, so they send Keycloak's Secure cookies.
    def return_ok_secure(self, cookie, request):
        return True

def session(user):
    opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar(LocalhostCookies())))
    # Authorization-code login as a browser would do it: Recepción -> Keycloak's form -> back.
    csrf = json.load(opener.open(base + '/api/auth/csrf', timeout=10))['csrfToken']
    body = urllib.parse.urlencode({'csrfToken': csrf, 'json': 'true', 'callbackUrl': base + '/inbox'}).encode()
    authorize = json.load(opener.open(urllib.request.Request(base + '/api/auth/signin/keycloak', data=body), timeout=20))['url']
    form = opener.open(authorize, timeout=20).read().decode()
    action = html.unescape(re.search(r'<form[^>]*\baction="([^"]+)"', form).group(1))
    body = urllib.parse.urlencode({'username': keycloak_dev.USERS[user], 'password': keycloak_dev.password(), 'credentialId': ''}).encode()
    opener.open(urllib.request.Request(action, data=body), timeout=20).read()
    assert json.load(opener.open(base + '/api/auth/session', timeout=10)).get('user'), 'Keycloak login failed for ' + user
    return opener

for endpoint in ['/api/conversations', '/api/kapso/stream']:
    try:
        session('admin').open(base + endpoint + '?phoneNumberId=' + env['KAPSO_PHONE_NUMBER_ID'], timeout=10)
        raise AssertionError('Another hospital was granted access')
    except urllib.error.HTTPError as error:
        assert error.code == 403

client = session('hospital')
result = json.load(client.open(base + '/api/conversations?phoneNumberId=' + env['KAPSO_PHONE_NUMBER_ID'], timeout=15))
assert isinstance(result['data'], list)
public = env['KAPSO_WEBHOOK_URL']
assert public.startswith('https://') and public.endswith('/webhooks/kapso')
for path in ['/', '/api/conversations', '/_inbox_events']:
    try:
        urllib.request.urlopen(public.removesuffix('/webhooks/kapso') + path, timeout=15)
        raise AssertionError('Unexpected public route')
    except urllib.error.HTTPError as error:
        assert error.code == 404

with client.open(base + '/api/kapso/stream?phoneNumberId=' + env['KAPSO_PHONE_NUMBER_ID'], timeout=15) as stream:
    assert stream.headers['content-type'] == 'text/event-stream'
    assert stream.readline().startswith(b'retry:')
    marker = str(uuid.uuid4())
    body = json.dumps({'phone_number_id': env['KAPSO_PHONE_NUMBER_ID'], 'conversation': {'id': marker, 'phone_number_id': env['KAPSO_PHONE_NUMBER_ID']}}, separators=(',', ':')).encode()
    headers = {'Content-Type': 'application/json', 'X-Webhook-Event': 'whatsapp.conversation.created', 'X-Idempotency-Key': str(uuid.uuid4()), 'X-Webhook-Payload-Version': 'v2'}
    try:
        urllib.request.urlopen(urllib.request.Request(public, data=body, headers=headers), timeout=15)
        raise AssertionError('Unsigned event accepted')
    except urllib.error.HTTPError as error:
        assert error.code == 401
    headers['X-Webhook-Signature'] = hmac.new(env['KAPSO_WEBHOOK_SECRET'].encode(), body, hashlib.sha256).hexdigest()
    with urllib.request.urlopen(urllib.request.Request(public, data=body, headers=headers), timeout=15) as response:
        assert response.status == 200
    while True:
        line = stream.readline()
        assert line, 'SSE closed before delivery'
        if line.startswith(b'data:'):
            event = json.loads(line[5:])
            if event.get('conversationId') == marker:
                assert event['phoneNumberId'] == env['KAPSO_PHONE_NUMBER_ID']
                break
print(json.dumps({'crossTenantDenied': True, 'privatePathsBlocked': True, 'unsignedRejected': True, 'publicWebhookToSse': True, 'conversations': len(result['data']), 'manualSendEnabled': result['manualSendEnabled'], 'outboundMessagesSent': 0}))
