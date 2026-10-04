"""Opt-in commercial journey with synthetic Hospital C data. No electronic charges or WhatsApp.
Keeps audited completed purchases; deactivates synthetic catalogue/company fixtures after checks.
"""
import json,pathlib,subprocess,sys,urllib.request,urllib.parse,urllib.error,uuid
ROOT=pathlib.Path(__file__).resolve().parents[1]
TENANT='cccccccc-cccc-4ccc-8ccc-cccccccccccc'
def envfile(path):
    return dict((k,v.strip().strip('"\'')) for k,v in (line.split('=',1) for line in path.read_text().splitlines() if line and not line.startswith('#') and '=' in line))
def run(config):
    crm='http://recepcion-api-1:8080'; hospital='http://hospital-api-1:8080';kc='http://hospital-keycloak-1:8080'
    def call(base,path,method='GET',body=None,token=None,form=False):
        headers={'Content-Type':'application/x-www-form-urlencoded' if form else 'application/json'}
        if token:headers['Authorization']='Bearer '+token
        data=None if body is None else (urllib.parse.urlencode(body) if form else json.dumps(body)).encode()
        try:
            with urllib.request.urlopen(urllib.request.Request(base+path,data=data,headers=headers,method=method),timeout=40) as r:
                raw=r.read();return r.status,json.loads(raw) if raw else None
        except urllib.error.HTTPError as e:
            return e.code,None
    def ok(result,label,status=200):
        assert result[0]==status,f'{label}: HTTP {result[0]}, expected {status}'
        print('PASS '+label,flush=True);return result[1]
    def login(user):return ok(call(kc,'/realms/hospital/protocol/openid-connect/token','POST',{'grant_type':'password','client_id':'hospital-web','username':user,'password':config['hospital_password']},form=True),'Synthetic '+user+' login')['access_token']
    admin=login('dev-administrador-c');foreign=login('dev-administrador-a');doctor=login('dev-medicos-c')
    reception=config['reception']
    assert ok(call(crm,'/api/me',token=reception),'Reception C tenant')['tenant']['id']==TENANT
    tag=uuid.uuid4().hex[:8].upper();company=None;service=None
    try:
        company=ok(call(hospital,'/v1/commercial/companies','POST',{'name':'Empresa sintética '+tag,'taxId':'TEST-'+tag,'email':'','phone':''},admin),'Hospital create company',201)
        ok(call(hospital,'/v1/commercial/companies/'+company['id'],token=foreign),'Hospital company tenant isolation',404)
        ok(call(hospital,'/v1/commercial/services','POST',{'code':'FORBIDDEN','name':'Forbidden','kind':'consultation','price':100,'currency':'USD'},doctor),'Doctor cannot write masters',403)
        company=ok(call(hospital,'/v1/commercial/companies/'+company['id']+'/agreement','PUT',{'version':company['version'],'name':'Convenio general sintético','discountPercent':15,'active':True},admin),'Hospital single percentage agreement')
        ok(call(hospital,'/v1/commercial/companies/'+company['id']+'/agreement','PUT',{'version':1,'name':'Stale agreement','discountPercent':90,'active':True},admin),'Agreement version prevents overwrite',409)
        service=ok(call(hospital,'/v1/commercial/services','POST',{'code':'CONS-'+tag,'name':'Consulta sintética '+tag,'kind':'consultation','price':100,'currency':'USD'},admin),'Hospital create priced consultation',201)
        companies=ok(call(crm,'/api/commercial/companies?q='+tag,token=reception),'Reception consumes Hospital companies')
        assert any(c['id']==company['id'] and c['agreement']['discountPercent']==15 for c in companies['items'])
        phone='5037'+str(int(tag,16)%10000000).zfill(7)
        contact=ok(call(crm,'/api/contacts','POST',{'name':'Cliente sintético '+tag,'phone':phone,'tags':'commercial-integration'},reception),'CRM create lead',201)
        assert not contact['isCustomer']
        opportunity=ok(call(crm,'/api/opportunities','POST',{'contactId':contact['id'],'title':'Compra sintética '+tag,'value':999,'stage':'won'},reception),'CRM won stage alone does not buy')
        context=ok(call(crm,'/api/contacts/'+contact['id']+'/context',token=reception),'CRM remains lead after won stage');assert not context['contact']['isCustomer']
        path='/api/commercial/opportunities/'+opportunity['id']
        quote=ok(call(crm,path+'/quote','POST',{'companyId':company['id'],'lines':[{'serviceId':service['id'],'quantity':2}]},reception),'Hospital calculates quote through Reception');assert quote['total']==170 and quote['discountPercent']==15
        service=ok(call(hospital,'/v1/commercial/services/'+service['id'],'PUT',{**service,'price':120},admin),'Hospital changes current price')
        confirm={'quoteVersion':quote['quoteVersion'],'paymentReceived':True,'paymentReference':'SYNTHETIC-'+tag}
        ok(call(crm,path+'/purchase','POST',confirm,reception),'Changed price rejects old quote before purchase',409)
        quote=ok(call(crm,path+'/quote','POST',{'companyId':company['id'],'lines':[{'serviceId':service['id'],'quantity':2}]},reception),'Requote after definitive rejection');assert quote['total']==204
        confirm['quoteVersion']=quote['quoteVersion']
        ok(call(crm,path+'/purchase','POST',{**confirm,'paymentReceived':False},reception),'Explicit received-payment confirmation required',400)
        purchase=ok(call(crm,path+'/purchase','POST',confirm,reception),'Record paid purchase in Hospital from Reception');assert purchase['status']=='completed' and purchase['quote']['total']==204
        repeat=ok(call(crm,path+'/purchase','POST',confirm,reception),'Repeat recovers same completed purchase');assert repeat['id']==purchase['id']
        stored=ok(call(hospital,'/v1/commercial/purchases/'+purchase['id'],token=admin),'Purchase persisted in Hospital');assert stored['quote']['total']==204
        context=ok(call(crm,'/api/contacts/'+contact['id']+'/context',token=reception),'Contact converted by Hospital evidence');assert context['contact']['isCustomer'] and context['contact']['hospitalCustomerId']==purchase['customerId'] and context['contact']['hospitalCompanyId']==company['id']
        assert len([a for a in context['activities'] if a['kind']=='customer_converted'])==1 and len([a for a in context['activities'] if a['kind']=='purchase'])==1
        customer=ok(call(hospital,'/v1/commercial/customers/'+purchase['customerId'],token=admin),'Hospital commercial customer has no fictional patient');assert customer['patientId'] is None
        ok(call(hospital,'/v1/commercial/purchases/'+purchase['id'],token=foreign),'Purchase tenant isolation',404)
        customer=ok(call(hospital,'/v1/commercial/customers/'+customer['id']+'/company','PUT',{'version':customer['version'],'companyId':None},admin),'Hospital removes single company assignment')
        synced=ok(call(crm,'/api/commercial/contacts/'+contact['id']+'/sync','POST',{},reception),'Reception refreshes Hospital company');assert synced['hospitalCompanyId'] is None
        stored=ok(call(hospital,'/v1/commercial/purchases/'+purchase['id'],token=admin),'Purchase retains original agreement snapshot');assert stored['quote']['discountPercent']==15 and stored['quote']['total']==204
        # Verify explicit patient linking uses actual Hospital identity, not demographic creation.
        patient='01a0a362-0dd9-7418-a987-ad9c8e67758b'
        contacts=ok(call(crm,'/api/contacts',token=reception),'Read synthetic fixture contact')
        clinical=next(c for c in contacts if c.get('patientId')==patient)
        linked=ok(call(crm,'/api/contacts/'+clinical['id']+'/patient','POST',{'patientId':patient},reception),'Existing Hospital patient converts contact without purchase');assert linked['isCustomer']
        print('RESULT '+json.dumps({'companyId':company['id'],'serviceId':service['id'],'contactId':contact['id'],'opportunityId':opportunity['id'],'purchaseId':purchase['id']}),flush=True)
    finally:
        if service:
            current=ok(call(hospital,'/v1/commercial/services/'+service['id'],token=admin),'Read cleanup service')
            ok(call(hospital,'/v1/commercial/services/'+service['id'],'PUT',{**current,'active':False},admin),'Deactivate synthetic service; preserve purchase')
        if company:
            current=ok(call(hospital,'/v1/commercial/companies/'+company['id'],token=admin),'Read cleanup company')
            ok(call(hospital,'/v1/commercial/companies/'+company['id'],'PUT',{**current,'active':False},admin),'Deactivate synthetic company; preserve history')
if __name__=='__main__':
    if '--container' in sys.argv:run(json.load(sys.stdin))
    else:
        e=envfile(ROOT/'.env');h=envfile(ROOT.parent/'Hospital/.env');assert e.get('ASPNETCORE_ENVIRONMENT')=='Development' and e.get('DEV_HOSPITAL_TENANT_ID')==TENANT and h.get('KC_SEED_DEV_USERS')=='true'
        # Recepción only trusts Keycloak's public issuer, reachable from the host: mint its token here.
        import keycloak_dev
        cfg={'reception':keycloak_dev.token('hospital'),'hospital_password':h['KC_DEV_USERS_PASSWORD']}
        result=subprocess.run(['docker','run','--rm','-i','--network','hospital','-v',str(pathlib.Path(__file__).resolve())+':/tests/test.py:ro','python:3.12-slim','python','/tests/test.py','--container'],input=json.dumps(cfg),text=True)
        sys.exit(result.returncode)
