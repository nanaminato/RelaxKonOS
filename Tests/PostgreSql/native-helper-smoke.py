"""Run only inside a disposable systemd fixture; never a production host.
Create /etc/relaxkonos-db02-disposable explicitly before running. No secrets are used.
Usage: python3 native-helper-smoke.py /opt/acceptance/RelaxKonOS.PostgreSql.Tests [--major 16|17|18] [--existing]
"""
import argparse,base64,json,os,subprocess,uuid
from pathlib import Path
assert os.geteuid()==0 and Path('/etc/relaxkonos-db02-disposable').read_text().strip()=='DB-02'
parser=argparse.ArgumentParser()
parser.add_argument('binary')
parser.add_argument('--major',type=int,choices=(16,17,18),default=16)
parser.add_argument('--existing',action='store_true')
args=parser.parse_args()
binary=args.binary; major=args.major; count=0
def check(ok,label):
 global count
 assert ok,label
 count+=1; print('PASS '+label,flush=True)
def run(*args,input=None):
 return subprocess.run(args,input=input,text=True,capture_output=True,check=True).stdout.strip()
def call(kind='PostgreSqlQuery',options=None,id=None,extra=None):
 request={'operation':kind,'operationId':id or str(uuid.uuid4())}
 if options is not None: request['postgreSql']=options
 if extra:request.update(extra)
 file=Path('/tmp/db02-request.json');file.write_text(json.dumps(request))
 result=json.loads(run(binary,'helper-json',str(file)))
 if result.get('outputBase64'):result['state']=json.loads(base64.b64decode(result['outputBase64']))
 return result
def state():
 result=call(); assert result['success'],result.get('error'); return result['state']
def command(action,id=None,expected=None):
 before=state()
 options={'Confirmed':True,'Command':{'Action':action,'InstanceId':before['InstanceId'] or str(uuid.uuid4()),'ExpectedRevision':before['Revision'] if expected is None else expected,'Port':55432}}
 id=id or str(uuid.uuid4());return call('PostgreSqlExecute',options,id),id,options
s=state();check(s['Supported'],'real x64 systemd host accepted')
if not args.existing:
 check(not s['Installed'] and s['ProblemCode'] is None and s['Candidate'] is None
       and not Path('/var/lib/relaxkonos/postgresql-control/sources.list').exists(),'fresh status does not refresh APT')
 planned=call('PostgreSqlInstallPlan')
 check(planned['success'] and planned['state']['ProblemCode'] is None,f'fresh host offers signed candidate: {planned.get("state",{}).get("ProblemCode") or planned.get("error")}')
 id=str(uuid.uuid4());target=planned['state']['Candidate']
 check(target['PackageName']==f'postgresql-{major}','host offers the expected native PostgreSQL major')
 plan={'ComponentId':'postgresql','HostId':run('hostname'),'Change':'Install','ExpectedRevision':s['Revision'],'Mode':'SystemPackage','SourceId':None,'ExpectedSourceRevision':0,'ExpectedTrustRevision':0,'ReleaseId':None,'RequestedVersion':target['Version'],'KeepData':True,'IdempotencyKey':id}
 options={'Confirmed':True,'FrozenPackage':{'ComponentId':'postgresql','Request':plan,'Expected':{'Mode':'SystemPackage','Archive':None,'Package':target}}}
 installed=call('PostgreSqlExecute',options,id)
 check(installed['success'] and installed['state']['CommittedOperationId']==id,
       f'frozen signed package installation commits receipt: {installed.get("error")}')
 check(run('pg_lsclusters','--no-header')=='','package scripts did not create a default cluster')
 check(not Path('/etc/postgresql-common/createcluster.conf').exists(),'temporary suppression is cleaned after commit')
 replay=call('PostgreSqlExecute',options,id)
 check(replay['success'] and replay['state']['Revision']==installed['state']['Revision'],'install replay is idempotent across Helper processes')
 initialized,_,_=command('Initialize')
 check(initialized['success'] and initialized['state']['ConnectionVerified'],
       f'initialization verifies real local authentication: {initialized.get("error")}')
s=state();check(s['Installed'] and s['Managed'] and s['InstanceId'],'ledger owns a real initialized instance')
instance=s['InstanceId'].replace('-','');unit=f'postgresql@{major}-rk'+instance+'.service';socket='/run/postgresql/rk'+instance
def sql(text):return run('runuser','-u','postgres','--',f'/usr/lib/postgresql/{major}/bin/psql','-XAtw','-v','ON_ERROR_STOP=1','-h',socket,'-p','55432','-d','postgres',input=text)
check(sql('SHOW server_version_num;').startswith(str(major)),'connected server runs the requested major')
check(sql("SHOW listen_addresses;")=='127.0.0.1,::1','only loopback addresses listen')
check(sql("SHOW password_encryption;")=='scram-sha-256','SCRAM password storage configured')
sql('CREATE TABLE IF NOT EXISTS db02_acceptance(value int); INSERT INTO db02_acceptance VALUES (42);')
stopped,_,_=command('Stop');check(stopped['success'] and not stopped['state']['Running'],'stop confirms inactive systemd process')
started,id,options=command('Start');check(started['success'] and started['state']['ConnectionVerified'],'restart verifies native identity and authentication')
check(sql('SELECT count(*) FROM db02_acceptance WHERE value=42;')!='0','data survives service restart')
replay=call('PostgreSqlExecute',options,id);check(replay['success'] and replay['state']['Revision']==started['state']['Revision'],'same operation replays its durable receipt')
start_options=json.loads(json.dumps(options))
start_id=id
options['Command']['Action']='Stop'
check(not call('PostgreSqlExecute',options,id)['success'],'same operation cannot substitute a different payload')
check(not command('Stop',expected=1)[0]['success'],'stale state revision is rejected')
check(not call(extra={'path':'/tmp/forbidden'})['success'],'mixed Helper request is rejected')
verified,verifyid,_=command('Verify');check(verified['success'],'explicit verification commits original receipt')
recoveryid=str(uuid.uuid4()); recovered=call('PostgreSqlRecover',{'Confirmed':True,'RecoverOperationId':verifyid},recoveryid)
check(recovered['success'] and call(id=recoveryid)['state']['CommittedOperationId']==recoveryid,'recovery has a separate durable receipt')
# A real verification failure leaves a resumable checkpoint. Restore the native service externally
# only in this disposable fixture, then resume the exact original pending operation.
check(command('Stop')[0]['success'],'prepare stopped recovery fixture')
failed,pendingid,_=command('Verify');check(not failed['success'] and state()['PendingOperationId']==pendingid,'verification failure preserves pending intent')
check(not command('Start')[0]['success'],'new work cannot bypass pending intent')
run('systemctl','start',unit)
recovered=call('PostgreSqlRecover',{'Confirmed':True,'RecoverOperationId':pendingid})
check(recovered['success'] and recovered['state']['PendingOperationId'] is None and recovered['state']['ConnectionVerified'],'safe checkpoint resumes across Helper processes')
# Drift must block writes without overwriting the changed file.
auto=Path('/var/lib/relaxkonos/databases/'+instance+'/data/postgresql.auto.conf');saved=auto.read_bytes()
try:
 auto.write_bytes(saved+b'\n# fixture drift\n')
 check(state()['ProblemCode']=='database.configuration_drift','auto configuration drift detected')
 check(not command('Stop')[0]['success'],'configuration drift blocks mutation')
finally:auto.write_bytes(saved)
check(state()['ConnectionVerified'] and state()['PendingOperationId'] is None,'restored exact configuration verifies without an extra pending transaction')
# Explicit fixture injection models a crash after dispatch but before a completion checkpoint.
# It is not a claim that an actual power loss was exercised.
ledger=Path('/var/lib/relaxkonos/postgresql-control/state.json');original=ledger.read_bytes()
try:
 data=json.loads(original);pending_id=str(uuid.uuid4())
 identity=next(r['Identity'] for r in data['Receipts'] if r['Id']==start_id)
 data['Pending']={'Id':pending_id,'Identity':identity,'Request':start_options,'Phase':'StartDispatched'}
 ledger.write_text(json.dumps(data));pid=run('systemctl','show','-p','MainPID','--value',unit)
 uncertain=call('PostgreSqlRecover',{'Confirmed':True,'RecoverOperationId':pending_id})
 check(not uncertain['success'] and state()['PendingOperationId']==pending_id,'uncertain dispatch is not replayed or reported as success')
 check(run('systemctl','show','-p','MainPID','--value',unit)==pid,'uncertain recovery leaves the existing process untouched')
finally:ledger.write_bytes(original)
check(state()['PendingOperationId'] is None,'fixture finishes without a pending transaction')
if major in (17,18) and not args.existing:
    maintenance=call('PostgreSqlMaintenancePlan')
    check(maintenance['success'],f'native maintenance candidate resolves: {maintenance.get("error")}')
    check(maintenance['state']['Candidate']['PackageName']==f'postgresql-{major}'
          and maintenance['state']['Candidate']['Suite']==target['Suite'],'maintenance stays on the native source and major')
    check(not call('PostgreSqlMigrationPlan')['success'],'Noble PGDG migration is unavailable on native newer majors')
    before=state(); operation=str(uuid.uuid4())
    repair=call('PostgreSqlExecute',{'Confirmed':True,'Maintenance':{'Action':'Repair',
        'InstanceId':before['InstanceId'],'ExpectedRevision':before['Revision'],'Target':maintenance['state']['Candidate']}},operation)
    check(repair['success'] and repair['state']['CommittedOperationId']==operation,
          f'native repair commits a receipt: {repair.get("error")}')
    check(not repair['state']['Running'] and any(b['RestoreVerified'] for b in repair['state']['Backups']),
          'repair leaves the cluster stopped with a verified backup')
    check(command('Start')[0]['success'] and sql('SELECT count(*) FROM db02_acceptance WHERE value=42;')!='0',
          'data survives native same-version repair')
print('Native Helper checks passed: '+str(count),flush=True)
