"""DB-06 destructive acceptance; ONLY in a disposable Ubuntu24.04 systemd fixture.
The --old-fixture phase deliberately constructs an OLD owned 16.2 cluster using signed
Ubuntu packages and a test-only ledger rewrite. Product installation never downgrades.
All lifecycle assertions after fixture construction call the actual Helper.
"""
import base64,hashlib,json,os,subprocess,sys,uuid
from pathlib import Path
assert os.geteuid()==0 and Path('/etc/relaxkonos-db02-disposable').read_text().strip()=='DB-02'
binary='/opt/acceptance/RelaxKonOS.PostgreSql.Tests'; ledger=Path('/var/lib/relaxkonos/postgresql-control/state.json');count=0

def run(*args,input=None):return subprocess.run(args,input=input,text=True,capture_output=True,check=True).stdout.strip()
def check(ok,label):
 global count
 assert ok,label
 count+=1;print('PASS '+label,flush=True)
def call(kind='PostgreSqlQuery',options=None,id=None):
 r={'operation':kind,'operationId':id or str(uuid.uuid4())}
 if options is not None:r['postgreSql']=options
 v=json.loads(run(binary,'helper-stdin',input=json.dumps(r)))
 if v.get('outputBase64'):v['state']=json.loads(base64.b64decode(v['outputBase64']))
 return v
def state():
 r=call();assert r['success'],r.get('error');return r['state']
def action(name):
 s=state();return call('PostgreSqlExecute',{'Confirmed':True,'Command':{'Action':name,'InstanceId':s['InstanceId'] or str(uuid.uuid4()),'ExpectedRevision':s['Revision'],'Port':55432}})
def maintenance(name,**kw):
 s=state();c={'Action':name,'InstanceId':s['InstanceId'],'ExpectedRevision':s['Revision']};c.update(kw)
 if name in ['Update','Repair','Reinstall']:
  p=call('PostgreSqlMaintenancePlan');assert p['success'],p.get('error');c['Target']=p['state']['Candidate']
 return call('PostgreSqlExecute',{'Confirmed':True,'Maintenance':c})
def sql(text):
 s=state();return run('runuser','-u','postgres','--','/usr/lib/postgresql/16/bin/psql','-XAtw','-v','ON_ERROR_STOP=1','-h','/run/postgresql/rk'+s['InstanceId'].replace('-',''),'-p','55432','-d','postgres',input=text)
def require(result,label):check(result['success'],label+': '+str(result.get('error','')));return result['state']

if '--old-fixture' in sys.argv:
 s=state();assert s['InstanceId'] and not s['PendingOperationId'] and not s['Databases'] and not s['Accounts']
 instance=s['InstanceId'].replace('-','')
 run('pg_dropcluster','--stop','16','rk'+instance)
 suppress=Path('/etc/postgresql-common/createcluster.conf');assert not suppress.exists();suppress.write_text('create_main_cluster = false\n')
 try:run('apt-get','install','--yes','--allow-downgrades','postgresql-16=16.2-1ubuntu4','postgresql-client-16=16.2-1ubuntu4')
 finally:suppress.unlink()
 l=json.loads(ledger.read_text());l['Instance']=None;l['ConfigurationHash']=None;l['Package']['Version']='16.2-1ubuntu4';l['BinaryHash']=hashlib.sha256(Path('/usr/lib/postgresql/16/bin/postgres').read_bytes()).hexdigest().upper();ledger.write_text(json.dumps(l))
 require(action('Initialize'),'real 16.2 initdb fixture is product-initialized')
 check(sql('SHOW server_version;').startswith('16.2 '),'source cluster actually runs PostgreSQL 16.2')
 sql("CREATE TABLE db06_values(id int primary key, value text); INSERT INTO db06_values VALUES (1,'before-patch');")
 print('OLD FIXTURE READY',flush=True);sys.exit(0)

if '--prepare-restart' in sys.argv or '--prepare-restore-restart' in sys.argv:
 import time,socket
 s=state()
 if not s['Running']:require(action('Start'),'prepare interruption running instance')
 sql("CREATE TABLE IF NOT EXISTS db06_interrupt(id int); TRUNCATE db06_interrupt; INSERT INTO db06_interrupt VALUES(64);")
 backup=None
 if '--prepare-restore-restart' in sys.argv:
  backup=require(maintenance('Backup'),'prepare verified restore target')['Backups'][-1]['Id']
  require(action('Start'),'start restore interruption source');sql('UPDATE db06_interrupt SET id=99;')
 options={'Confirmed':True,'Maintenance':{'Action':'Backup','InstanceId':s['InstanceId'],'ExpectedRevision':state()['Revision']}}
 if backup:options['Maintenance'].update(Action='Restore',BackupId=backup)
 proc=subprocess.Popen([binary,'helper-stdin'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
 proc.stdin.write(json.dumps({'operation':'PostgreSqlExecute','operationId':str(uuid.uuid4()),'postgreSql':options}));proc.stdin.close()
 deadline=time.monotonic()+40
 while time.monotonic()<deadline:
  l=json.loads(ledger.read_text())
  if (l.get('Pending') or {}).get('Phase') in (['RestoreDispatched','Restored'] if backup else ['MaintenanceStopped']):proc.kill();proc.wait();break
  if proc.poll() is not None:raise RuntimeError('operation finished before interruption')
  time.sleep(.001)
 else:raise RuntimeError('checkpoint timeout')
 unit='postgresql@16-rk'+s['InstanceId'].replace('-','')+'.service'
 check(Path('/etc/systemd/system',unit).is_symlink(),'interrupted real Helper retains persistent mask')
 for family,host in [(socket.AF_INET,'127.0.0.1'),(socket.AF_INET6,'::1')]:
  with socket.socket(family) as conn:
   conn.settimeout(1);check(conn.connect_ex((host,55432))!=0,'interruption has no TCP listener '+host)
 print('RESTART FIXTURE NOW',flush=True);sys.exit(0)
if '--after-restart' in sys.argv:
 import socket
 s=state();unit='postgresql@16-rk'+s['InstanceId'].replace('-','')+'.service'
 check(Path('/etc/systemd/system',unit).is_symlink() and s['PendingMaintenance'] is not None,'mask and original maintenance survive systemd restart')
 check(subprocess.run(['systemctl','start',unit],capture_output=True).returncode!=0,'normal service cannot bypass interrupted maintenance')
 for family,host in [(socket.AF_INET,'127.0.0.1'),(socket.AF_INET6,'::1')]:
  with socket.socket(family) as conn:
   conn.settimeout(1);check(conn.connect_ex((host,55432))!=0,'restarted fixture has no TCP listener '+host)
 r=require(call('PostgreSqlRecover',{'Confirmed':True,'RecoverOperationId':s['PendingOperationId']}),'original maintenance resumes after restart')
 check(not r['PendingOperationId'] and not r['Running'] and not Path('/etc/systemd/system',unit).is_symlink(),'completion cleans mask and remains stopped')
 require(action('Start'),'explicit start after interrupted maintenance')
 check(sql('SELECT id FROM db06_interrupt;')=='64','interruption and recovery preserve original row')

if '--update' in sys.argv:
 check(state()['Version']=='16.2-1ubuntu4','cross-patch source is old package')
 r=require(maintenance('Update'),'same-major cross-patch update commits')
 check(r['Version']=='16.15-0ubuntu0.24.04.1' and not r['Running'],'updated package observed and service stays stopped')
 check(len(r['Backups'])==1 and r['Backups'][0]['RestoreVerified'],'pre-update old-version backup was restored in isolation')
 require(action('Start'),'updated instance starts')
 check(sql('SELECT value FROM db06_values WHERE id=1;')=='before-patch','pre-update row and index survive real binary replacement')
 check(sql('SHOW server_version;').startswith('16.15 '),'server reports actual new patch')
 old=r['Backups'][0]['Id'];check(not maintenance('Restore',BackupId=old)['success'],'old-package backup cannot restore under new binaries')

if '--credential-gate' in sys.argv:
 import secrets
 if not state()['Running']:require(action('Start'),'start credential gate fixture')
 database='g'+uuid.uuid4().hex[:10];account='u'+uuid.uuid4().hex[:10];password=secrets.token_urlsafe(24);previous=password
 def account_call(kind,reference=None,secret=None):
  s=state();cmd={'Action':kind,'InstanceId':s['InstanceId'],'ExpectedRevision':s['Revision'],'Database':database}
  if kind!='CreateDatabase':cmd.update(Account=account,CredentialReference=reference)
  if kind=='CreateAccount':cmd['Permission']='ReadOnly'
  req={'operation':'PostgreSqlExecute','operationId':str(uuid.uuid4()),'postgreSql':{'Confirmed':True,'AccountCommand':cmd}}
  if secret:req['postgreSqlSecrets']=secret
  result=json.loads(run(binary,'helper-stdin',input=json.dumps(req)))
  check(result['success'],'credential gate '+kind)
 account_call('CreateDatabase');account_call('CreateAccount',str(uuid.uuid4()),{'Password':password})
 backup=require(maintenance('Backup'),'backup with original credential reference')['Backups'][-1]['Id']
 require(action('Start'),'start for normal password rotation')
 account_call('ChangePassword',str(uuid.uuid4()),{'Password':secrets.token_urlsafe(24),'PreviousPassword':previous})
 check(not maintenance('Restore',BackupId=backup)['success'] and not state()['PendingOperationId'],'restore cannot revive a password revoked after backup')

if '--failed-backup' in sys.argv:
 if not state()['Running']:require(action('Start'),'start failure fixture')
 sql('ALTER ROLE postgres NOLOGIN;')
 r=maintenance('Backup');s=state();instance=s['InstanceId'].replace('-','');unit='postgresql@16-rk'+instance+'.service'
 check(not r['success'] and s['PendingMaintenance'] is not None,'failed verification retains original maintenance')
 check(Path('/etc/systemd/system',unit).is_symlink() and not Path('/var/lib/relaxkonos/databases',instance,'data/postmaster.pid').exists(),'failure leaves persistent mask and no private process')
 run('runuser','-u','postgres','--','/usr/lib/postgresql/16/bin/postgres','--single','-D','/var/lib/relaxkonos/databases/'+instance+'/data','-c','config_file=/etc/postgresql/16/rk'+instance+'/postgresql.conf','postgres',input='ALTER ROLE postgres LOGIN;')
 require(call('PostgreSqlRecover',{'Confirmed':True,'RecoverOperationId':s['PendingOperationId']}),'original failed backup resumes after fixture identity is repaired')
 check(not state()['PendingOperationId'] and not state()['Running'],'failed backup compensation completes in stopped state')

if '--path-gates' in sys.argv:
 s=state()
 if s['Running']:require(action('Stop'),'stop for path protection fixture')
 root=Path('/var/lib/relaxkonos/databases')/s['InstanceId'].replace('-','')/'data'
 external=Path('/tmp/db06-external');external.mkdir(exist_ok=True);marker=external/'sentinel';marker.write_text('external')
 link=root/'db06-link';link.symlink_to(external,target_is_directory=True)
 try:check(not maintenance('UninstallDeleteData',DeleteConfirmation=s['InstanceId'].replace('-',''))['success'] and marker.read_text()=='external','symlink blocks destructive maintenance without touching target')
 finally:link.unlink()
 mount=root/'db06-mount';mount.mkdir();run('mount','--bind',str(external),str(mount))
 try:check(not maintenance('UninstallDeleteData',DeleteConfirmation=s['InstanceId'].replace('-',''))['success'] and marker.read_text()=='external','nested mount blocks destructive maintenance without touching target')
 finally:run('umount',str(mount));mount.rmdir()
 require(action('Start'),'start after rejected path fixtures')
 sql("CREATE TABLE IF NOT EXISTS db06_values(id int primary key,value text); INSERT INTO db06_values VALUES(1,'backup-value') ON CONFLICT(id) DO NOTHING;")

if '--lifecycle' in sys.argv:
 if not state()['Running']:require(action('Start'),'prepare running instance')
 sql("UPDATE db06_values SET value='backup-value' WHERE id=1;")
 r=require(maintenance('Backup'),'stopped consistent backup passes restore exercise');backup=r['Backups'][-1]['Id']
 require(action('Start'),'start for post-backup mutation');sql("UPDATE db06_values SET value='after-backup' WHERE id=1;")
 require(maintenance('Restore',BackupId=backup),'restore selected same-version backup')
 require(action('Start'),'start restored data');check(sql('SELECT value FROM db06_values WHERE id=1;')=='backup-value','restore returns exact backed-up row')
 # An unrelated native cluster must survive and block maintenance before a pending intent is created.
 run('pg_createcluster','16','external','--port','55433')
 try:
  check(not maintenance('UninstallKeepData')['success'] and not state()['PendingOperationId'],'external native cluster blocks package removal before dispatch')
  check(Path('/var/lib/postgresql/16/external/PG_VERSION').exists(),'external cluster data untouched')
 finally:run('pg_dropcluster','16','external')
 # Same-version repair must actually replace a damaged server executable.
 require(action('Stop'),'stop before damaged binary fixture')
 executable=Path('/usr/lib/postgresql/16/bin/postgres');original=executable.read_bytes();executable.write_bytes(b'broken-db06-fixture')
 require(maintenance('Repair'),'same-version repair replaces damaged binary without running it')
 check(executable.read_bytes()==original,'repaired executable equals original trusted bytes')
 require(action('Start'),'repaired engine starts');check(sql('SELECT value FROM db06_values WHERE id=1;')=='backup-value','repair preserves restored data')
 # Tampering with a backup must be rejected before taking maintenance ownership.
 root=Path('/var/lib/relaxkonos/postgresql-control/backups')/state()['InstanceId'].replace('-','')/backup.replace('-','')
 f=root/'data/PG_VERSION';saved=f.read_bytes();f.write_bytes(b'99\n')
 try:check(not maintenance('Restore',BackupId=backup)['success'] and not state()['PendingOperationId'],'changed backup bytes block restore before dispatch')
 finally:f.write_bytes(saved)
 r=require(maintenance('UninstallKeepData'),'uninstall keeps owned data and backup')
 check(r['PackageRemoved'] and not r['Installed'] and not r['DataDeleted'],'retained instance is represented explicitly')
 check(not executable.exists(),'engine package executable removed')
 require(maintenance('Reinstall'),'reinstall retained instance without initdb')
 require(action('Start'),'retained instance restarts');check(sql('SELECT value FROM db06_values WHERE id=1;')=='backup-value','retained row survives uninstall and reinstall')
 check(not maintenance('UninstallDeleteData',DeleteConfirmation='wrong')['success'],'destructive uninstall requires exact instance confirmation')
 instance=state()['InstanceId'].replace('-','')
 r=require(maintenance('UninstallDeleteData',DeleteConfirmation=instance),'confirmed destructive uninstall')
 check(r['DataDeleted'] and not r['Accounts'] and not r['Backups'],'deleted data leaves explicit tombstone without references')
 check(not Path('/var/lib/relaxkonos/databases',instance,'data').exists() and not Path('/etc/postgresql/16','rk'+instance).exists() and not Path('/var/lib/relaxkonos/postgresql-control/backups',instance).exists(),'only owned data config and backups deleted')
 require(maintenance('Reinstall'),'reinstall after destructive removal')
 check(state()['InstanceId'] is None,'deleted instance is never implicitly recreated')
 require(action('Initialize'),'fresh explicit initialization works after delete and reinstall')
print('DB-06 checks passed: '+str(count),flush=True)
