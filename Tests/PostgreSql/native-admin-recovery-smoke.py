"""DB-04 acceptance; disposable systemd fixture only. Run --prepare-restart then restart container and --after-restart."""
import base64,json,os,subprocess,sys,uuid,socket,time,secrets
from pathlib import Path
assert os.geteuid()==0 and Path('/etc/relaxkonos-db02-disposable').read_text().strip()=='DB-02'
binary=sys.argv[1];mode=sys.argv[2] if len(sys.argv)>2 else '--normal';checks=0
root=Path('/var/lib/relaxkonos/postgresql-control'); path=root/'state.json'
def check(ok,label):
 global checks
 assert ok,label
 checks+=1;print('PASS '+label,flush=True)
def run(*args,input=None):
 return subprocess.run(args,input=input,text=True,capture_output=True,check=True).stdout.strip()
def call(options=None,id=None,recover=False):
 request={'operation':'PostgreSqlRecover' if recover else 'PostgreSqlExecute' if options else 'PostgreSqlQuery','operationId':id or str(uuid.uuid4())}
 if options:request['postgreSql']=options
 result=json.loads(run(binary,'helper-stdin',input=json.dumps(request)))
 if result.get('outputBase64'):result['state']=json.loads(base64.b64decode(result['outputBase64']))
 return result
def state():return call()['state']
s=state();inst=s['InstanceId'];port=s['Port'];name='rk'+inst.replace('-','');unit='postgresql@16-'+name+'.service'
data=Path('/var/lib/relaxkonos/databases')/inst.replace('-','')/'data';config=Path('/etc/postgresql/16')/name
mask=Path('/etc/systemd/system')/unit
sock='/run/postgresql/'+name

def admin(sql):return run('runuser','-u','postgres','--','/usr/lib/postgresql/16/bin/psql','-XqAtw','-v','ON_ERROR_STOP=1','-h',sock,'-p',str(port),'-d','postgres',input=sql)
def start():
 s=state();r=call({'Confirmed':True,'Command':{'Action':'Start','InstanceId':inst,'ExpectedRevision':s['Revision'],'Port':port}})
 check(r['success'] and r['state']['ConnectionVerified'],'explicit normal start verifies peer authentication')
def offline():
 for family,host in [(socket.AF_INET,'127.0.0.1'),(socket.AF_INET6,'::1')]:
  with socket.socket(family) as conn:
   conn.settimeout(1)
   check(conn.connect_ex((host,port))!=0,'no TCP listener on '+host)
def request():
 s=state();return {'Confirmed':True,'AdminRecovery':{'InstanceId':inst,'ExpectedRevision':s['Revision']}}
def recover():
 s=state();return call({'Confirmed':True,'RecoverOperationId':s['PendingOperationId']},recover=True)

if mode=='--normal':
 if not s['Running']:start()
 admin('CREATE TABLE IF NOT EXISTS public.db04_sentinel(id integer); TRUNCATE public.db04_sentinel; INSERT INTO public.db04_sentinel VALUES (41);')
 admin("ALTER ROLE postgres PASSWORD '"+secrets.token_hex(24)+"' NOLOGIN;")
 check(not state()['ConnectionVerified'],'lost administrator cannot authenticate')
 before=(config/'pg_hba.conf').read_bytes();id=str(uuid.uuid4());options=request();r=call(options,id)
 check(r['success'] and r['state']['CommittedOperationId']==id and not r['state']['Running'],'recovery commits while leaving service stopped')
 check(not mask.is_symlink() and not (data/'postmaster.pid').exists(),'private process and persistent mask cleaned')
 check(before==(config/'pg_hba.conf').read_bytes() and b'trust' not in before,'normal HBA unchanged and no temporary trust')
 offline();start()
 check(admin("SELECT rolsuper AND rolcanlogin AND rolpassword IS NULL FROM pg_authid WHERE oid=10;")=='t','fixed administrator restored with no password')
 check(admin('SELECT count(*) FROM public.db04_sentinel;')=='1','application data retained')
 check(call(options,id)['success'],'original successful recovery is idempotent')
 bad=request();bad['AdminRecovery']['InstanceId']=str(uuid.uuid4())
 check(not call(bad)['success'] and state()['PendingOperationId'] is None,'foreign instance rejected before mutation')
 # Damage role identity rather than adding test-only fault injection to production.
 run('systemctl','stop',unit)
 run('runuser','-u','postgres','--','/usr/lib/postgresql/16/bin/postgres','--single','-D',str(data),'-c','config_file='+str(config/'postgresql.conf'),'postgres',input="UPDATE pg_authid SET rolname='displaced_admin' WHERE oid=10;\n")
 r=call(request())
 check(not r['success'] and state()['PendingAdminRecovery'] is not None,'unrecognized role identity fails with original pending operation')
 check(mask.is_symlink() and os.readlink(mask)=='/dev/null','failure retains persistent systemd isolation')
 offline()
 # Repair only fixture damage offline; production never guesses renamed roles.
 sql="UPDATE pg_authid SET rolname='postgres' WHERE oid=10;\n"
 run('runuser','-u','postgres','--','/usr/lib/postgresql/16/bin/postgres','--single','-D',str(data),'-c','config_file='+str(config/'postgresql.conf'),'postgres',input=sql)
 r=recover();check(r['success'] and r['state']['PendingOperationId'] is None,'same recovery continues after fixture identity corrected')
 start()
 accounts=state().get('Accounts') or []
 if accounts:
  account=accounts[0]
  admin('ALTER ROLE "'+account['Name']+'" SUPERUSER; ALTER ROLE postgres NOLOGIN;')
  r=call(request())
  check(not r['success'] and state()['PendingAdminRecovery'] is not None,'dependent account drift prevents recovery success')
  check(mask.is_symlink() and not (data/'postmaster.pid').exists(),'verification failure stops private server and keeps mask')
  check(before==(config/'pg_hba.conf').read_bytes(),'verification failure leaves original authentication unchanged')
  offline()
  run('runuser','-u','postgres','--','/usr/lib/postgresql/16/bin/postgres','--single','-D',str(data),'-c','config_file='+str(config/'postgresql.conf'),'postgres',input='UPDATE pg_authid SET rolsuper=false WHERE oid='+str(account['Oid'])+';\n')
  r=recover();check(r['success'] and r['state']['PendingOperationId'] is None,'failed verification resumes after owned account repaired')
  start()
elif mode=='--prepare-restart':
 if not s['Running']:start()
 admin('ALTER ROLE postgres NOLOGIN;')
 # Real Helper stopped as soon as its durable isolation checkpoint appears.
 proc=subprocess.Popen([binary,'helper-stdin'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
 proc.stdin.write(json.dumps({'operation':'PostgreSqlExecute','operationId':str(uuid.uuid4()),'postgreSql':request()}));proc.stdin.close()
 deadline=time.monotonic()+30
 while time.monotonic()<deadline:
  ledger=json.loads(path.read_text())
  if (ledger.get('Pending') or {}).get('Phase') in ['AdminIsolated','AdminRepairDispatched','AdminRepaired']:
   proc.kill();proc.wait();break
  if proc.poll() is not None:raise RuntimeError('operation finished before kill checkpoint')
  time.sleep(.001)
 else:raise RuntimeError('checkpoint timeout')
 check(mask.is_symlink(),'interrupted real Helper leaves persistent mask')
 offline()
elif mode=='--after-restart':
 check(mask.is_symlink(),'mask survives container/systemd restart')
 check(s['PendingAdminRecovery'] is not None,'original recovery survives restart')
 offline()
 check(subprocess.run(['systemctl','start',unit],capture_output=True).returncode!=0,'normal service cannot bypass recovery isolation')
 r=recover();check(r['success'] and r['state']['PendingOperationId'] is None,'original recovery resumes after restart')
 check(not mask.is_symlink(),'only owned isolation mask removed after verification')
 offline();start()
 check(admin('SELECT count(*) FROM public.db04_sentinel;')=='1','data survives interrupted recovery and restart')
else:raise RuntimeError('unknown mode')
print('DB-04 '+mode+' checks passed: '+str(checks))
