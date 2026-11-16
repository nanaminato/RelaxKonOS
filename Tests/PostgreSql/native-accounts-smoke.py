"""DB-03 native acceptance. Disposable DB-02 fixture only. Secrets never go in argv or output."""
import base64,json,os,secrets,subprocess,sys,uuid
from pathlib import Path
assert os.geteuid()==0 and Path('/etc/relaxkonos-db02-disposable').read_text().strip()=='DB-02'
binary=sys.argv[1]; checks=0
ledger_path=Path('/var/lib/relaxkonos/postgresql-control/state.json')
def check(ok,label):
 global checks
 if not ok:raise RuntimeError(label)
 checks+=1;print('PASS '+label,flush=True)
def call(options=None,secret=None,id=None,recover=False):
 request={'operation':'PostgreSqlRecover' if recover else 'PostgreSqlExecute' if options else 'PostgreSqlQuery','operationId':id or str(uuid.uuid4())}
 if options:request['postgreSql']=options
 if secret:request['postgreSqlSecrets']=secret
 p=subprocess.run([binary,'helper-stdin'],input=json.dumps(request),text=True,capture_output=True)
 if p.returncode:raise RuntimeError('Helper worker failed')
 result=json.loads(p.stdout)
 if result.get('outputBase64'):result['state']=json.loads(base64.b64decode(result['outputBase64']))
 return result
def state():
 result=call();check(result['success'],'state query succeeded');return result['state']
def action(kind,db,account=None,permission=None,password=None,old=None):
 s=call()['state'];id=str(uuid.uuid4())
 cmd={'Action':kind,'InstanceId':s['InstanceId'],'ExpectedRevision':s['Revision'],'Database':db,'Account':account,'Permission':permission,'CredentialReference':str(uuid.uuid4()) if kind in ['CreateAccount','ChangePassword'] else None}
 options={'Confirmed':True,'AccountCommand':cmd};secret={'Password':password,'PreviousPassword':old} if password else None
 return call(options,secret,id),id,options,secret
s=state();check(s['ConnectionVerified'] and not s['PendingOperationId'],'managed instance is ready')
socket='/run/postgresql/rk'+s['InstanceId'].replace('-','');port=str(s['Port'])
def admin(db,sql):
 p=subprocess.run(['runuser','-u','postgres','--','/usr/lib/postgresql/16/bin/psql','-XqAtw','-v','ON_ERROR_STOP=1','-h',socket,'-p',port,'-d',db],input=sql,text=True,capture_output=True)
 if p.returncode:raise RuntimeError('fixture administrator SQL failed')
 return p.stdout.strip()
def login(db,name,pw,sql):
 file=Path('/run/db03-'+str(uuid.uuid4()))
 try:
  fd=os.open(file,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
  with os.fdopen(fd,'w') as f:f.write('127.0.0.1:'+port+':'+db+':'+name+':'+pw.replace('\\','\\\\').replace(':','\\:')+'\n')
  p=subprocess.run(['/usr/lib/postgresql/16/bin/psql','-XqAtw','-v','ON_ERROR_STOP=1','-h','127.0.0.1','-p',port,'-U',name,'-d',db],input=sql,text=True,capture_output=True,env={**os.environ,'PGPASSFILE':str(file),'PGCONNECT_TIMEOUT':'5'})
  return p.returncode,p.stdout.strip()
 finally:file.unlink(missing_ok=True)
# Unique names allow independent repeat runs without taking over any prior fixture resource.
prefix='d'+uuid.uuid4().hex[:8]; db=prefix+'a';other=prefix+'b';reader=prefix+'r';writer=prefix+'w';migrator=prefix+'m'
passwords={name:secrets.token_urlsafe(28) for name in [reader,writer,migrator]}
for database in [db,other]:
 result,_,_,_=action('CreateDatabase',database)
 check(result['success'],'create isolated owned database '+database)
owner='rk_owner_'+db
admin(db,'SET ROLE '+owner+'; CREATE TABLE app.existing(id serial PRIMARY KEY,value text); INSERT INTO app.existing(value) VALUES (\'fixture\');')
for name,permission in [(reader,'ReadOnly'),(writer,'ReadWrite'),(migrator,'Migration')]:
 result,id,options,secret=action('CreateAccount',db,name,permission,passwords[name])
 check(result['success'],'create and authenticate '+permission+' account')
 before=result['state']['Revision'];replay=call(options,secret,id)
 check(replay['success'] and replay['state']['Revision']==before,'same account operation is idempotent')
 check(passwords[name] not in json.dumps(result) and passwords[name] not in ledger_path.read_text(),'no password in state or Helper ledger')
check(login(db,reader,passwords[reader],'SELECT count(*) FROM app.existing;')==(0,'1'),'reader can read existing data')
check(login(db,reader,passwords[reader],"INSERT INTO app.existing(value) VALUES ('denied');")[0]!=0,'reader cannot write')
check(login(db,writer,passwords[writer],"INSERT INTO app.existing(value) VALUES ('allowed'); UPDATE app.existing SET value='updated' WHERE id=2; DELETE FROM app.existing WHERE id=2;")[0]==0,'writer can insert update and delete with sequence')
for sql in ['CREATE TABLE app.denied(id int);','CREATE DATABASE denied_db;','CREATE ROLE denied_role;','SET ROLE '+owner+';','TRUNCATE app.existing;']:
 check(login(db,writer,passwords[writer],sql)[0]!=0,'daily account denies DDL privilege or owner escalation')
for database in [other,'postgres','template1']:
 check(login(database,writer,passwords[writer],'SELECT 1;')[0]!=0,'daily account cannot connect to '+database)
check(login(db,migrator,passwords[migrator],'CREATE TABLE app.no_implicit_owner(id int);')[0]!=0,'migration ownership is not implicitly inherited')
check(login(db,migrator,passwords[migrator],'SET ROLE '+owner+'; CREATE TABLE app.future(id serial PRIMARY KEY,value text); INSERT INTO app.future(value) VALUES (\'future\');')[0]==0,'migration explicitly assumes owner and creates future objects')
check(login(db,reader,passwords[reader],'SELECT count(*) FROM app.future;')==(0,'1'),'future object grants reach reader')
check(login(db,writer,passwords[writer],"INSERT INTO app.future(value) VALUES ('future write');")[0]==0,'future table and sequence grants reach writer')
result,_,_,_=action('SetGrant',db,writer,'ReadOnly',passwords[writer]);check(result['success'],'grant template changes transactionally')
check(login(db,writer,passwords[writer],"INSERT INTO app.future(value) VALUES ('denied');")[0]!=0,'downgraded account cannot write existing object')
admin(db,'SET ROLE '+owner+'; CREATE TABLE app.after_downgrade(id int);')
check(login(db,writer,passwords[writer],'INSERT INTO app.after_downgrade VALUES (1);')[0]!=0,'downgraded default privileges deny future writes')
old=passwords[writer];new=secrets.token_urlsafe(28)
old_account=dict(next(a for a in call()['state']['Accounts'] if a['Name']==writer))
keepfile=Path('/run/db03-session-'+str(uuid.uuid4()))
fd=os.open(keepfile,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
with os.fdopen(fd,'w') as f:f.write('127.0.0.1:'+port+':'+db+':'+writer+':'+old+'\n')
session=subprocess.Popen(['/usr/lib/postgresql/16/bin/psql','-XqAtw','-h','127.0.0.1','-p',port,'-U',writer,'-d',db],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,env={**os.environ,'PGPASSFILE':str(keepfile),'PGCONNECT_TIMEOUT':'5'})
session.stdin.write("SELECT 'ready';\n");session.stdin.flush()
check(session.stdout.readline().strip()=='ready','existing application session established before rotation')
keepfile.unlink()
result,rotate_id,rotate_options,rotate_secret=action('ChangePassword',db,writer,password=new,old=old)
check(result['success'],'password rotation verifies new authentication and old rejection')
session.stdin.write('SELECT current_user;\n');session.stdin.flush()
check(session.stdout.readline().strip()==writer,'existing authenticated session remains usable after normal rotation')
session.stdin.close();session.wait(timeout=10)
check(login(db,writer,new,'SELECT 1;')==(0,'1') and login(db,writer,old,'SELECT 1;')[0]!=0,'fresh connections accept new password and reject old password')
account=next(a for a in result['state']['Accounts'] if a['Name']==writer)
check(account['CredentialRevision']==2 and account['ApplicationsNeedUpdate'],'database success explicitly leaves dependent application update pending')
check(new not in ledger_path.read_text() and old not in ledger_path.read_text(),'password rotation leaves no plaintext credential in Helper ledger')
# Model a lost Helper response after the database committed, using a real SQL witness and a prior filesystem checkpoint.
current=ledger_path.read_bytes();data=json.loads(current)
receipt=next(x for x in data['Receipts'] if x['Id']==rotate_id)
data['Receipts']=[x for x in data['Receipts'] if x['Id']!=rotate_id]
data['Revision']-=1
data['Pending']={'Id':rotate_id,'Identity':receipt['Identity'],'Request':rotate_options,'Phase':'AccountDispatched'}
# Restore prior non-secret account metadata; native password and in-database witness stay committed.
a=next(x for x in data['Accounts'] if x['Name']==writer);a.update(old_account)
try:
 ledger_path.write_text(json.dumps(data))
 failed=call({'Confirmed':True,'RecoverOperationId':rotate_id},{'Password':secrets.token_urlsafe(28),'PreviousPassword':old},recover=True)
 check(not failed['success'] and call()['state']['PendingOperationId']==rotate_id,'committed native change with failed credential verification remains pending')
 recovered=call({'Confirmed':True,'RecoverOperationId':rotate_id},rotate_secret,recover=True)
 check(recovered['success'] and recovered['state']['PendingOperationId'] is None,'native SQL witness reconciles lost response without password replay')
 check(login(db,writer,new,'SELECT 1;')[0]==0,'recovered password remains the staged candidate')
finally:ledger_path.write_bytes(current)
foreign=prefix+'foreign'
admin('postgres','CREATE DATABASE '+foreign+';')
try:
 rejected,_,_,_=action('CreateAccount',db,prefix+'blocked','ReadOnly',secrets.token_urlsafe(28))
 check(not rejected['success'] and call()['state']['PendingOperationId'] is None,'foreign PUBLIC-connect database blocks new account before mutation')
 check(admin('postgres',"SELECT count(*) FROM pg_roles WHERE rolname='"+prefix+"blocked';")=='0','rejected account is not created')
finally:admin('postgres','DROP DATABASE '+foreign+';')
check(not list(Path('/run/relaxkonos-postgresql-secrets').iterdir()),'ephemeral verification passfiles are removed')
log='\n'.join(p.read_text(errors='replace') for p in Path('/var/log/postgresql').glob('*.log'))
check(all(pw not in log for pw in [*passwords.values(),new]),'database logs contain no plaintext test password')
print('DB-03 native checks passed: '+str(checks),flush=True)
