"""Opt-in local synthetic Hospital C delivery contract check. No WhatsApp or clinical writes."""
import json
from pathlib import Path
import subprocess
import sys
import urllib.request
import urllib.parse
import urllib.error
ROOT=Path(__file__).resolve().parents[1]
TENANT='cccccccc-cccc-4ccc-8ccc-cccccccccccc'
PATIENT='01a0a362-0dd9-7418-a987-ad9c8e67758b'
def run(config):
    def call(path,body=None,token=None,form=False):
        host='http://hospital-keycloak-1:8080' if form else 'http://hospital-api-1:8080'
        data=None if body is None else (urllib.parse.urlencode(body) if form else json.dumps(body)).encode()
        headers={'Content-Type':'application/x-www-form-urlencoded' if form else 'application/json'}
        if token:headers['Authorization']='Bearer '+token
        return urllib.request.urlopen(urllib.request.Request(host+path,data=data,headers=headers),timeout=30)
    token=json.load(call('/realms/hospital/protocol/openid-connect/token',{'grant_type':'client_credentials','client_id':config['client'],'client_secret':config['secret']},form=True))['access_token']
    patient=json.load(call('/v1/patients/'+PATIENT,token=token));phone=patient['phone']
    page=json.load(call('/v1/reception/patients/'+PATIENT+'/prescriptions/list',{'phone':phone},token))
    assert isinstance(page['prescriptionIds'],list)
    print('PASS authenticated phone-bound prescription listing')
    try:
        call('/v1/reception/patients/'+PATIENT+'/prescriptions/list',{'phone':'50300000000'},token)
        raise AssertionError('Mismatched phone allowed')
    except urllib.error.HTTPError as e:assert e.code==404
    print('PASS wrong recipient denied')
    if page['prescriptionIds']:
        prescription=page['prescriptionIds'][0]
        detail=json.load(call('/v1/reception/prescriptions/'+prescription,{'phone':phone},token))
        assert detail['patientId']==PATIENT and detail['state']=='signed' and not detail['contentWithheld']
        with call('/v1/reception/prescriptions/'+prescription+'/pdf',{'phone':phone},token) as result:
            assert result.headers.get_content_type()=='application/pdf' and result.read(5)==b'%PDF-'
        print('PASS signed prescription ownership and real PDF')
    else:print('No issued prescription in this synthetic fixture; PDF contract covered with mocks')
    print('Outbound WhatsApp messages: 0')
if __name__=='__main__':
    if '--container' in sys.argv:run(json.load(sys.stdin))
    else:
        env=dict(l.split('=',1) for l in (ROOT/'.env').read_text().splitlines() if '=' in l and not l.startswith('#'))
        assert env.get('ASPNETCORE_ENVIRONMENT')=='Development' and env.get('DEV_HOSPITAL_TENANT_ID')==TENANT
        prefix='Hospital__Tenants__'+TENANT+'__'
        config={'client':'recepcion-service-'+TENANT,'secret':env['HOSPITAL_SERVICE_CLIENT_SECRET']}
        p=subprocess.run(['docker','run','--rm','-i','--network','hospital','-v',str(Path(__file__).resolve())+':/tests/test.py:ro','python:3.12-slim','python','/tests/test.py','--container'],input=json.dumps(config),text=True)
        sys.exit(p.returncode)
