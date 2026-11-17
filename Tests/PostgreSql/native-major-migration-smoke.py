"""Destructive DB-07B acceptance; disposable Ubuntu/systemd fixture only."""
import base64,json,os,subprocess,sys,uuid,socket,time
from pathlib import Path
assert os.geteuid()==0 and Path('/etc/relaxkonos-db02-disposable').read_text().strip()=='DB-02'
binary='/opt/acceptance/RelaxKonOS.PostgreSql.Tests';count=0
context=Path('/var/lib/relaxkonos/db07b-test.json')
def check(ok,label):
 global count
 assert ok,label
 count+=1;print('PASS '+label,flush=True)
def run(*args,input=None):return subprocess.run(args,input=input,text=True,capture_output=True,check=True).stdout.strip()
def call(kind='PostgreSqlQuery',options=None,id=None):
 request={'operation':kind,'operationId':id or str(uuid.uuid4())}
 if options:request['postgreSql']=options
 result=json.loads(run(binary,'helper-stdin',input=json.dumps(request)))
 if result.get('outputBase64'):result['state']=json.loads(base64.b64decode(result['outputBase64']))
 return result
def state():
 r=call();assert r['success'],r.get('error');return r['state']
def require(r,label):check(r['success'],label+': '+str(r.get('error')));return r.get('state')
def execute(action,**kw):
 s=state();c={'Action':action,'InstanceId':s['InstanceId'],'ExpectedRevision':s['Revision']};c.update(kw)
 return call('PostgreSqlExecute',{'Confirmed':True,'Maintenance':c})
def sql(s,query):
 major=s['Version'].split('.')[0];instance=s['InstanceId'].replace('-','')
 return run('runuser','-u','postgres','--',f'/usr/lib/postgresql/{major}/bin/psql','-XqAtw','-v','ON_ERROR_STOP=1','-h','/run/postgresql/rk'+instance,'-p',str(s['Port']),'-d','postgres',input=query)
def offline(port):
 for host in ['127.0.0.1','::1']:
  sock=socket.socket(socket.AF_INET6 if ':' in host else socket.AF_INET,socket.SOCK_STREAM);sock.settimeout(1)
  try:result=sock.connect_ex((host,port))
  finally:sock.close()
  check(result!=0,'migration exposes no TCP on '+host)
def masks(s):
 name='rk'+s['InstanceId'].replace('-','')
 for major in [16,18]:
  unit=f'postgresql@{major}-{name}.service'
  check(Path('/etc/systemd/system/'+unit).is_symlink() and os.readlink('/etc/systemd/system/'+unit)=='/dev/null','persistent mask covers major '+str(major))
  check(subprocess.run(['systemctl','start',unit],capture_output=True).returncode!=0,'ordinary start refused for major '+str(major))
mode=sys.argv[1] if len(sys.argv)>1 else '--normal'
s=state()
if mode in ['--normal','--prepare-restart']:
 check(s['Version']=='16.15-0ubuntu0.24.04.1' and s['ConnectionVerified'],'owned PostgreSQL 16 source is ready')
 sql(s,'CREATE TABLE db07b_sentinel(id integer PRIMARY KEY); INSERT INTO db07b_sentinel VALUES(718);')
 plan=require(call('PostgreSqlMigrationPlan'),'resolve signed exact major target')
 check(plan['Candidate']['Version']=='18.7-1.pgdg24.04+2','target pins verified PGDG release')
 check(not execute('MigrateMajor',Target=plan['Candidate'])['success'],'missing source confirmation rejected')
 check(state()['PendingOperationId'] is None and state()['Running'],'invalid intent has no native side effect')
 require(execute('ConfigureBackupSchedule',Schedule={'Enabled':True,'IntervalHours':24,'KeepLast':2,'AllowDowntime':False}),'enable old-version backup policy')
 s=state();command={'Action':'MigrateMajor','InstanceId':s['InstanceId'],'ExpectedRevision':s['Revision'],'Target':plan['Candidate'],'MigrationSourceFingerprint':'B97B0AFCAA1A47F044F244A07FCC7D46ACCC4CF8'}
 request={'Confirmed':True,'Maintenance':command};operation=str(uuid.uuid4());context.write_text(json.dumps({'request':request,'operation':operation,'source':s}))
 if mode=='--prepare-restart':
  payload={'operation':'PostgreSqlExecute','operationId':operation,'postgreSql':request}
  process=subprocess.Popen([binary,'helper-stdin'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
  process.stdin.write(json.dumps(payload));process.stdin.close()
  ledger=Path('/var/lib/relaxkonos/postgresql-control/state.json');deadline=time.time()+600
  while time.time()<deadline:
   current=json.loads(ledger.read_text())
   if (current.get('Pending') or {}).get('Phase')=='MajorUpgraded':
    process.kill();process.wait();break
   if process.poll() is not None:raise RuntimeError('helper finished before interruption: '+process.stdout.read())
   time.sleep(.001)
  else:process.kill();raise RuntimeError('interruption timeout')
  check(state()['PendingOperationId']==operation,'real Helper interruption retains original migration')
  check(json.loads(ledger.read_text())['Pending']['Phase']=='MajorUpgraded','interrupted after native copy before activation')
  offline(s['Port']);masks(s)
  print('DB-07B prepare-restart checks: '+str(count),flush=True);sys.exit(0)
 result=require(call('PostgreSqlExecute',request,operation),'migrate real cluster and commit receipt')
 verify_after=True
elif mode in ['--recover','--after-restart']:
 saved=json.loads(context.read_text());offline(s['Port']);masks(s);check(s['PendingOperationId']==saved['operation'],'original migration survives restart');require(call('PostgreSqlRecover',{'Confirmed':True,'RecoverOperationId':saved['operation']}),'recover original migration')
elif mode=='--ca-precondition':
 bundle=Path('/etc/ssl/certs/ca-certificates.crt');saved_bundle=Path('/etc/ssl/certs/db07b-test-ca.saved');assert not saved_bundle.exists()
 bundle.rename(saved_bundle)
 try:
  result=call('PostgreSqlMigrationPlan');check(not result['success'] and result.get('error')=='database.migration_ca_required','missing CA has explicit prerequisite failure')
  check(state()['Revision']==s['Revision'] and state()['PendingOperationId'] is None,'source prerequisite failure does not create migration work')
 finally:saved_bundle.rename(bundle)
elif mode=='--post-migration':
 def start():
  current=state()
  if not current['Running']:require(call('PostgreSqlExecute',{'Confirmed':True,'Command':{'Action':'Start','InstanceId':current['InstanceId'],'ExpectedRevision':current['Revision'],'Port':current['Port']}}),'start current major')
 start();plan=require(call('PostgreSqlMaintenancePlan'),'resolve migrated-major maintenance target')
 check(plan['Candidate']['PackageName']=='postgresql-18','maintenance stays in migrated major')
 require(execute('Repair',Target=plan['Candidate']),'repair migrated major');start()
 current=state();require(call('PostgreSqlExecute',{'Confirmed':True,'AdminRecovery':{'InstanceId':current['InstanceId'],'ExpectedRevision':current['Revision']}}),'administrator recovery uses migrated major');start()
 require(execute('ConfigureBackupSchedule',Schedule={'Enabled':True,'IntervalHours':24,'KeepLast':1,'AllowDowntime':True}),'authorize new-major backup policy')
 current=state();policy=current['BackupSchedule']
 require(call('PostgreSqlExecute',{'Confirmed':True,'Maintenance':{'Action':'ScheduledBackup','InstanceId':current['InstanceId'],'ExpectedRevision':0,'ScheduleId':policy['Id'],'DueAt':policy['NextDueAt']}}),'new-major scheduled backup restores service')
 check(state()['ConnectionVerified'] and sql(state(),'SELECT id FROM db07b_sentinel;')=='718','new-major recovery and backup preserve data')
 require(execute('UninstallKeepData'),'remove migrated engine retaining data')
 check(not state()['BackupSchedule']['Enabled'],'retained removal revokes new-major policy')
 plan=require(call('PostgreSqlMaintenancePlan'),'resolve migrated-major reinstall')
 require(execute('Reinstall',Target=plan['Candidate']),'reinstall migrated major');start()
 check(sql(state(),'SELECT id FROM db07b_sentinel;')=='718','reinstalled migrated major preserves data')
 require(execute('UninstallDeleteData',DeleteConfirmation=state()['InstanceId'].replace('-','')),'delete owned migrated instance')
 check(state()['DataDeleted'] and state()['BackupSchedule'] is None,'new and pre-migration backups and policy are removed with owned data')
elif mode=='--receipt-window':
 saved=json.loads(context.read_text());operation=saved['operation'];ledger=Path('/var/lib/relaxkonos/postgresql-control/state.json');value=json.loads(ledger.read_text())
 check(value['Pending'] is None and s['Version'].startswith('18.'),'receipt-window fixture starts from completed migration')
 receipt=next(r for r in value['Receipts'] if r['Id']==operation)
 value['Receipts']=[r for r in value['Receipts'] if r['Id']!=operation]
 value['Pending']={'Id':operation,'Identity':receipt['Identity'],'Request':saved['request'],'Phase':'Verified','RestartAfterBackup':False}
 ledger.write_text(json.dumps(value))
 require(call('PostgreSqlRecover',{'Confirmed':True,'RecoverOperationId':operation}),'reconcile durable verification before receipt commit')
 name='rk'+s['InstanceId'].replace('-','')
 check(all(not Path(f'/etc/systemd/system/postgresql@{major}-{name}.service').is_symlink() for major in [16,18]),'receipt reconciliation never recreates service masks')
 check(state()['PendingOperationId'] is None,'receipt reconciliation clears original pending responsibility')
elif mode=='--verify':
 verify_after=True
else:raise RuntimeError('unknown mode')
if mode in ['--normal','--verify','--after-restart']:
 saved=json.loads(context.read_text());s=saved['source'];request=saved['request'];operation=saved['operation'];result=state()
 check(result['Version']=='18.7-1.pgdg24.04+2' and not result['Running'] and result['PendingOperationId'] is None,'migration commits new major and remains stopped')
 check(result['Databases']==s['Databases'] and result['Accounts']==s['Accounts'],'database and role identities and credential references preserved')
 check(not result['BackupSchedule']['Enabled'],'migration revokes old automatic schedule')
 offline(result['Port'])
 check(call('PostgreSqlExecute',request,operation)['success'],'same migration intent returns original receipt')
 s=state();require(call('PostgreSqlExecute',{'Confirmed':True,'Command':{'Action':'Start','InstanceId':s['InstanceId'],'ExpectedRevision':s['Revision'],'Port':s['Port']}}),'start migrated instance')
 s=state();check(s['ConnectionVerified'] and sql(s,'SELECT id FROM db07b_sentinel;')=='718','native PostgreSQL 18 serves preserved data')
 check(not execute('Restore',BackupId=operation)['success'],'old physical recovery point cannot downgrade new data')
 check(sql(s,"SELECT count(*) FROM pg_hba_file_rules WHERE error IS NOT NULL OR auth_method='trust';")=='0','normal authentication has no temporary trust rules')
 require(execute('Backup'),'new-major physical backup verifies')
print('DB-07B '+mode+' checks: '+str(count),flush=True)
