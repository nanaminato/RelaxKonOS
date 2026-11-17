"""DB-07A native acceptance. Destructive fault fixtures: disposable Ubuntu/systemd container only.
No wall-clock changes: explicitly reconfiguring a policy makes a new immediate slot due.
"""
import base64,json,os,socket,subprocess,sys,time,uuid
from pathlib import Path
assert os.geteuid()==0 and Path('/etc/relaxkonos-db02-disposable').read_text().strip()=='DB-02'
binary='/opt/acceptance/RelaxKonOS.PostgreSql.Tests';root=Path('/var/lib/relaxkonos/postgresql-control');ledger=root/'state.json';checks=0

def check(ok,label):
 global checks
 assert ok,label
 checks+=1;print('PASS '+label,flush=True)
def run(*args,input=None):return subprocess.run(args,input=input,text=True,capture_output=True,check=True).stdout.strip()
def call(options=None,kind=None,id=None):
 request={'operation':kind or ('PostgreSqlExecute' if options else 'PostgreSqlQuery'),'operationId':id or str(uuid.uuid4())}
 if options:request['postgreSql']=options
 result=json.loads(run(binary,'helper-stdin',input=json.dumps(request)))
 if result.get('outputBase64'):result['state']=json.loads(base64.b64decode(result['outputBase64']))
 return result
def state():
 r=call();assert r['success'],r.get('error');return r['state']
def require(r,label):check(r['success'],label+': '+str(r.get('error')));return r['state']
def maintain(action,**fields):
 s=state();c={'Action':action,'InstanceId':s['InstanceId'],'ExpectedRevision':s['Revision']};c.update(fields)
 return call({'Confirmed':True,'Maintenance':c})
def configure(enabled=True,keep=2,downtime=True):return require(maintain('ConfigureBackupSchedule',Schedule={'Enabled':enabled,'IntervalHours':1,'KeepLast':keep,'AllowDowntime':downtime}),'configure explicit ongoing policy')
def scheduled():
 s=state();p=s['BackupSchedule'];return {'Confirmed':True,'Maintenance':{'Action':'ScheduledBackup','InstanceId':s['InstanceId'],'ExpectedRevision':0,'ScheduleId':p['Id'],'DueAt':p['NextDueAt']}}
def cycle():return require(call(scheduled()),'run exact due slot')
def action(name):
 s=state();return require(call({'Confirmed':True,'Command':{'Action':name,'InstanceId':s['InstanceId'],'ExpectedRevision':s['Revision'],'Port':s['Port']}}),name)
def sql(text):
 s=state();return run('runuser','-u','postgres','--','/usr/lib/postgresql/16/bin/psql','-XAtw','-v','ON_ERROR_STOP=1','-h','/run/postgresql/rk'+s['InstanceId'].replace('-',''),'-p',str(s['Port']),'-d','postgres',input=text)
def backup_path(s,id):return root/'backups'/s['InstanceId'].replace('-','')/id.replace('-','')
def offline(port):
 for family,host in [(socket.AF_INET,'127.0.0.1'),(socket.AF_INET6,'::1')]:
  with socket.socket(family) as conn:conn.settimeout(1);check(conn.connect_ex((host,port))!=0,'no TCP listener '+host)

mode=sys.argv[1] if len(sys.argv)>1 else '--normal'
s=state();unit='postgresql@16-rk'+s['InstanceId'].replace('-','')+'.service';mask=Path('/etc/systemd/system')/unit
if mode=='--normal':
 if not s['Running']:action('Start')
 sql('CREATE TABLE IF NOT EXISTS db07_sentinel(id int); TRUNCATE db07_sentinel; INSERT INTO db07_sentinel VALUES(707);')
 manual=require(maintain('Backup'),'manual recovery point')['Backups'][-1]['Id'];action('Start')
 configure(downtime=False);before=state();pid=run('systemctl','show','--value','-p','MainPID',unit);options=scheduled();operation=str(uuid.uuid4())
 skipped=require(call(options,id=operation),'stopped-only policy skips live instance')
 check(skipped['BackupSchedule']['LastOutcome']=='database.backup_schedule_running' and skipped['Running'] and run('systemctl','show','--value','-p','MainPID',unit)==pid,'skip leaves actual running process unchanged')
 check(len(skipped['Backups'])==len(before['Backups']),'skipped slot creates no recovery point')
 check(call(options,id=operation)['success'] and state()['Revision']==skipped['Revision'],'completed slot replay is idempotent')
 check(not call(scheduled())['success'],'next future slot cannot run early')
 action('Stop');configure(downtime=False);stopped=cycle()
 check(not stopped['Running'] and stopped['BackupSchedule']['LastOutcome']=='database.backup_schedule_verified','stopped-only policy backs up stopped instance without starting it')
 action('Start')
 for i in range(3):
  configure(keep=2,downtime=True);s=cycle()
  check(s['Running'] and s['ConnectionVerified'],'authorized downtime restores prior running state')
  check(sql('SELECT id FROM db07_sentinel;')=='707','automatic backup preserves representative data')
  check(any(b['Id']==manual for b in s['Backups']) and backup_path(s,manual).exists(),'automatic retention preserves manual recovery point')
 check(len([b for b in s['Backups'] if b['Automatic']])==2,'retention keeps exactly two verified automatic copies')
 check(all(b['RestoreVerified'] for b in s['Backups'] if b['Automatic']),'automatic copies passed real isolated restore checks')
 old=scheduled();configure(enabled=False);check(not call(old)['success'] and not state()['PendingOperationId'],'revoked generation cannot run a stale slot')
 require(maintain('DeleteBackup',BackupId=manual),'explicit deletion of selected backup')
 check(not backup_path(state(),manual).exists() and sql('SELECT id FROM db07_sentinel;')=='707','manual deletion removes only the selected copy')
 saved=ledger.read_bytes();l=json.loads(saved);l['BackupSchedule']['KeepLast']=0;ledger.write_text(json.dumps(l))
 try:check(not call()['success'],'corrupt retention grant fails closed')
 finally:ledger.write_bytes(saved)
 check(state()['PendingOperationId'] is None,'normal suite leaves no pending work')
elif mode=='--prepare-restart':
 if not s['Running']:action('Start')
 configure(keep=2,downtime=True);options=scheduled();operation=str(uuid.uuid4())
 proc=subprocess.Popen([binary,'helper-stdin'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
 proc.stdin.write(json.dumps({'operation':'PostgreSqlExecute','operationId':operation,'postgreSql':options}));proc.stdin.close()
 deadline=time.monotonic()+40
 while time.monotonic()<deadline:
  l=json.loads(ledger.read_text())
  if (l.get('Pending') or {}).get('Phase')=='MaintenanceStopped':proc.kill();proc.wait();break
  if proc.poll() is not None:raise RuntimeError('missed interruption checkpoint')
  time.sleep(.001)
 else:raise RuntimeError('interruption timeout')
 check(mask.is_symlink(),'real Helper interruption preserves isolation mask')
 check(json.loads(ledger.read_text())['Pending']['RestartAfterBackup'],'prior running state is durable before stop')
 offline(s['Port'])
elif mode=='--after-restart':
 check(mask.is_symlink() and s['PendingMaintenance']['Action']=='ScheduledBackup','root policy and original scheduled operation survive restart')
 offline(s['Port']);original=s['PendingOperationId'];r=require(call({'Confirmed':True,'RecoverOperationId':original},kind='PostgreSqlRecover'),'resume original scheduled backup')
 check(r['BackupSchedule']['LastOperationId']==original and r['Running'] and r['ConnectionVerified'],'recovered backup restores original running state and same operation')
 check(not mask.is_symlink() and not r['PendingOperationId'],'successful recovery clears only its isolation and pending intent')
 check(sql('SELECT id FROM db07_sentinel;')=='707','data survives real Helper and systemd restart')
elif mode=='--prepare-cleanup-restart':
 if not s['Running']:action('Start')
 manual=require(maintain('Backup'),'manual copy before retention fault')['Backups'][-1]['Id'];action('Start')
 s=state();old=min((b for b in s['Backups'] if b['Automatic']),key=lambda b:b['CreatedAt']);oldroot=backup_path(s,old['Id'])
 external=Path('/var/lib/relaxkonos/db07-external');external.mkdir(exist_ok=True);(external/'marker').write_text('keep')
 link=oldroot/'data/db07-link';link.symlink_to(external,target_is_directory=True)
 configure(keep=1,downtime=True);r=call(scheduled());s=state()
 check(not r['success'] and s['PendingOperationId'],'retention cleanup failure keeps original pending operation')
 check((external/'marker').read_text()=='keep' and link.is_symlink(),'retention cannot follow a foreign symlink')
 l=json.loads(ledger.read_text());check(any(b['Id']==old['Id'] for b in l['BackupDeletions']),'deletion ownership persists separately from restore references')
 check(all(b['Id']!=old['Id'] for b in s['Backups']) and any(b['Id']==manual for b in s['Backups']),'pending deletion is hidden from restore choices while manual copy remains')
 check(any(b['Automatic'] and b['RestoreVerified'] for b in s['Backups']),'new verified recovery point exists before any old deletion')
 Path('/var/lib/relaxkonos/db07-cleanup.json').write_text(json.dumps({'link':str(link),'oldroot':str(oldroot),'manual':manual,'operation':s['PendingOperationId']}));offline(s['Port'])
elif mode=='--after-cleanup-restart':
 context=json.loads(Path('/var/lib/relaxkonos/db07-cleanup.json').read_text());check(s['PendingOperationId']==context['operation'],'cleanup responsibility survives systemd restart')
 Path(context['link']).unlink();r=require(call({'Confirmed':True,'RecoverOperationId':context['operation']},kind='PostgreSqlRecover'),'resume original retention cleanup')
 check(r['Running'] and r['ConnectionVerified'] and not r['PendingOperationId'],'cleanup recovery restores service only after completing retention')
 check(not Path(context['oldroot']).exists() and Path('/var/lib/relaxkonos/db07-external/marker').read_text()=='keep','only owned obsolete recovery directory is removed')
 check(any(b['Id']==context['manual'] for b in r['Backups']) and len([b for b in r['Backups'] if b['Automatic']])==1,'manual backup and one verified automatic copy remain')
 check(not json.loads(ledger.read_text())['BackupDeletions'],'durable deletion queue drained')
 configure(enabled=False)
elif mode=='--restore-automatic':
 if not s['Running']:action('Start')
 assert not state()['BackupSchedule']['Enabled']
 backup=max((b for b in state()['Backups'] if b['Automatic']),key=lambda b:b['CreatedAt'])['Id']
 sql('UPDATE db07_sentinel SET id=999;')
 require(maintain('Restore',BackupId=backup),'restore a verified automatic recovery point')
 check(not state()['BackupSchedule']['Enabled'],'restoring data never revives revoked scheduling authorization')
 action('Start');check(sql('SELECT id FROM db07_sentinel;')=='707','automatic recovery point restores exact representative row')
elif mode=='--lifecycle':
 if not s['Running']:action('Start')
 configure(enabled=True,downtime=True)
 kept=require(maintain('UninstallKeepData'),'uninstall with an enabled backup policy')
 check(kept['PackageRemoved'] and not kept['BackupSchedule']['Enabled'],'package removal revokes future automatic work')
 target=require(call(kind='PostgreSqlMaintenancePlan'),'resolve retained-instance reinstall')['Candidate']
 reinstalled=require(maintain('Reinstall',Target=target),'reinstall retained instance')
 check(not reinstalled['BackupSchedule']['Enabled'],'reinstall does not revive prior ongoing authorization')
 action('Start');check(sql('SELECT id FROM db07_sentinel;')=='707','revocation and reinstall preserve existing data')
 configure(enabled=True,downtime=True)
 removed=require(maintain('UninstallDeleteData',DeleteConfirmation=state()['InstanceId'].replace('-','')),'delete isolated instance with policy')
 check(removed['DataDeleted'] and removed['BackupSchedule'] is None,'deleting an instance clears its persistent backup authorization')
else:raise RuntimeError('unknown mode')
print('DB-07 '+mode+' checks: '+str(checks),flush=True)
