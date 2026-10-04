"""Opt-in local CRUD against the real synthetic Hospital C; no WhatsApp messages.

Run after the operator provisions the service account: python3 tests/hospital_live.py
Creates a synthetic CRM contact if missing and cancels its test appointment at the end.
Only minimal integration credentials and two short-lived user tokens travel over stdin to an
ephemeral Docker verifier.
"""
import datetime as dt
import json
import pathlib
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid

TENANT = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc'
PATIENT = '01a0a362-0dd9-7418-a987-ad9c8e67758b'
ROOT = pathlib.Path(__file__).resolve().parents[1]


def run(config):
    crm = 'http://recepcion-api-1:8080'
    hospital = 'http://hospital-recepcion-api:8080'

    def request(base, path, method='GET', body=None, token=None, form=False):
        headers = {'Content-Type': 'application/x-www-form-urlencoded' if form else 'application/json'}
        if token:
            headers['Authorization'] = 'Bearer ' + token
        if method == 'POST':
            headers['Idempotency-Key'] = str(uuid.uuid4())
        data = None if body is None else (urllib.parse.urlencode(body) if form else json.dumps(body)).encode()
        try:
            with urllib.request.urlopen(urllib.request.Request(base + path, data=data, headers=headers, method=method), timeout=30) as res:
                raw = res.read()
                return res.status, json.loads(raw) if raw else None
        except urllib.error.HTTPError as exc:
            # Do not print response payloads: they can contain demographics.
            return exc.code, None

    def ok(result, label, status=200):
        assert result[0] == status, f'{label}: HTTP {result[0]}, expected {status}'
        print('PASS ' + label, flush=True)
        return result[1]

    token = ok(request('http://hospital-keycloak-1:8080', '/realms/hospital/protocol/openid-connect/token', 'POST',
                       {'grant_type': 'client_credentials', 'client_id': config['client'], 'client_secret': config['secret']}, form=True), 'Hospital service authentication')['access_token']
    if config.get('inspect'):
        day = dt.date.fromisoformat(config['inspect'])
        page = ok(request(hospital, f'/v1/agenda/patients/{PATIENT}?from={day}&to={day}', token=token), 'Read Hospital patient agenda')
        original = ok(request('http://hospital-api-1:8080', f'/v1/agenda/day?clinicalDay={day}', token=token), 'Read original Hospital agenda')
        for row in page['rows']:
            persisted = next(r for r in original['rows'] if r['appointmentId'] == row['appointmentId'])
            assert persisted['status'] == row['status'] and persisted['scheduledStart'] == row['scheduledStart']
        print('RESULT ' + json.dumps([{k: r[k] for k in ['appointmentId', 'scheduledStart', 'status']} for r in page['rows']]))
        return
    reception = config['reception']
    me = ok(request(crm, '/api/me', token=reception), 'CRM tenant binding')
    assert me['tenant']['id'] == TENANT
    patient = ok(request(hospital, '/v1/patients/' + PATIENT, token=token), 'Synthetic Hospital patient accessible')
    contacts = ok(request(crm, '/api/contacts', token=reception), 'CRM contacts')
    contact = next((c for c in contacts if c.get('patientId') == PATIENT), None)
    if contact is None:
        contact = ok(request(crm, '/api/contacts', 'POST', {'name': 'Paciente sintético · prueba agenda Hospital', 'phone': patient['phone'], 'tags': 'integration-test'}, reception), 'Create synthetic contact', 201)
        contact = ok(request(crm, '/api/contacts/' + contact['id'] + '/patient', 'POST', {'patientId': PATIENT}, reception), 'Link patient through verified phone')
    other = config['other']
    today = dt.datetime.now(dt.timezone(dt.timedelta(hours=-6))).date()
    start = today + dt.timedelta(days=2)
    end = start + dt.timedelta(days=10)
    availability = ok(request(hospital, f'/v1/agenda/booking-options?from={start}&to={end}', token=token), 'Real booking options')
    candidates = [(p['clinicianId'], s) for p in availability['professionals'] for d in p['days'] for s in d['slots'] if s['offered'] and s['takenBy'] == 0]
    assert len(candidates) >= 2, 'Two free future slots required'
    doctor, first = candidates[0]
    second = next(s for d, s in candidates[1:] if d == doctor and s['startsAt'] != first['startsAt'])
    path = f'/api/hospital/contacts/{contact["id"]}/appointments?from={start}&to={end}'
    ok(request(crm, path, token=other), 'Cross-tenant patient agenda rejected', 404)
    ok(request(hospital, f'/v1/agenda/patients/{PATIENT}?from={start}&to={start+dt.timedelta(days=31)}', token=token), 'Hospital rejects overlong range', 400)
    appointment = None
    cancelled = False
    def mutate(action, slot):
        return request(crm, '/api/hospital/appointments', 'POST', {'contactId': contact['id'], 'action': action, 'appointmentId': appointment, 'doctorId': doctor, 'startsAt': slot['startsAt'], 'durationMinutes': slot['durationMinutes']}, reception)
    def verify(slot, state):
        page = ok(request(crm, path, token=reception), 'Read patient agenda through Reception')
        row = next(r for r in page['rows'] if r['appointmentId'] == appointment)
        assert row['status'] == state
        assert row['clinicianId'] == doctor and row['durationMinutes'] == slot['durationMinutes']
        assert dt.datetime.fromisoformat(row['scheduledStart'].replace('Z', '+00:00')) == dt.datetime.fromisoformat(slot['startsAt'].replace('Z', '+00:00'))
        direct = ok(request(hospital, f'/v1/agenda/patients/{PATIENT}?from={start}&to={end}', token=token), 'Verify persisted Hospital agenda directly')
        assert next(r for r in direct['rows'] if r['appointmentId'] == appointment) == row
        original = ok(request('http://hospital-api-1:8080', f'/v1/agenda/{appointment}?patientId={PATIENT}', token=token), 'Verify same appointment in original Hospital API')
        assert original['status'] == state and original['patientId'] == PATIENT
    try:
        created = ok(mutate('create', first), 'CREATE through Reception')
        appointment = created['appointmentId']
        assert not created['overlaps']
        verify(first, 'booked')
        ok(mutate('reschedule', second), 'UPDATE through Reception')
        verify(second, 'booked')
        ok(mutate('cancel', second), 'CANCEL through Reception')
        cancelled = True
        verify(second, 'cancelled-by-patient')
        activities = ok(request(crm, '/api/activities?contactId=' + contact['id'], token=reception), 'CRM appointment history')
        for message in ['Cita registrada en Hospital.', 'Cita reprogramada en Hospital.', 'Cita cancelada en Hospital.']:
            assert any(a['kind'] == 'appointment' and a['body'] == message for a in activities)
        print('RESULT ' + json.dumps({'contactId': contact['id'], 'patientId': PATIENT, 'appointmentId': appointment, 'status': 'cancelled-by-patient', 'firstSlot': first, 'secondSlot': second, 'doctorId': doctor}), flush=True)
    finally:
        if appointment and not cancelled:
            status, _ = mutate('cancel', second)
            print('Cleanup cancellation HTTP ' + str(status), flush=True)


if __name__ == '__main__':
    if '--container' in sys.argv:
        run(json.load(sys.stdin))
    else:
        env = dict(line.split('=', 1) for line in (ROOT / '.env').read_text().splitlines() if line and not line.startswith('#') and '=' in line)
        assert env.get('ASPNETCORE_ENVIRONMENT') == 'Development' and env.get('DEV_HOSPITAL_TENANT_ID') == TENANT
        prefix = 'Hospital__Tenants__' + TENANT + '__'
        # Keycloak's public issuer is only reachable from the host, so user tokens are minted here.
        import keycloak_dev
        config = {'client': 'recepcion-service-' + TENANT, 'secret': env['HOSPITAL_SERVICE_CLIENT_SECRET']}
        if '--inspect' in sys.argv:
            config['inspect'] = sys.argv[sys.argv.index('--inspect') + 1]
        else:
            config.update(reception=keycloak_dev.token('hospital'), other=keycloak_dev.token('other'))
        result = subprocess.run(['docker', 'run', '--rm', '-i', '--network', 'hospital', '-v', str(pathlib.Path(__file__).resolve()) + ':/tests/test.py:ro', 'python:3.12-slim', 'python', '/tests/test.py', '--container'], input=json.dumps(config), text=True)
        sys.exit(result.returncode)
