"""DB-05 real remote acceptance. Docker fixtures with explicit task labels only; never a production host."""
import subprocess,json,base64,uuid,secrets,sys,hashlib
server,allowed,denied=sys.argv[1:4];binary='/opt/acceptance/RelaxKonOS.PostgreSql.Tests';checks=0

def run(args,input=None,ok=True):
 p=subprocess.run(args,input=input.encode('utf-8') if input is not None else None,capture_output=True)
 p.stdout=p.stdout.decode('utf-8');p.stderr=p.stderr.decode('utf-8')
 if ok and p.returncode:raise RuntimeError('fixture command failed: '+str(args[:4]))
 return p
for container,label in [(server,'db05'),(allowed,'db05-client'),(denied,'db05-client')]:
 info=json.loads(run(['docker','inspect',container]).stdout)[0]
 assert info['Config']['Labels'].get('relaxkonos.task')==label and not info['HostConfig']['PortBindings'] and not info['HostConfig'].get('Binds')
 assert not info['Mounts'] if container==server else all(m['Type']=='volume' and m['Destination']=='/var/lib/postgresql' and len(m['Name'])==64 for m in info['Mounts'])
network='rk-db05-net' if '--ipv6' in sys.argv else 'bridge'
ips={c:json.loads(run(['docker','inspect',c]).stdout)[0]['NetworkSettings']['Networks'][network]['GlobalIPv6Address' if '--ipv6' in sys.argv else 'IPAddress'] for c in [server,allowed,denied]}
def check(ok,label):
 global checks
 if not ok:raise RuntimeError(label)
 checks+=1;print('PASS '+label,flush=True)
def execs(*args,input=None,ok=True):return run(['docker','exec','-i',server,*args],input,ok)
def call(options=None,secret=None,tls=None,id=None,recover=False):
 req={'operation':'PostgreSqlRecover' if recover else 'PostgreSqlExecute' if options else 'PostgreSqlQuery','operationId':id or str(uuid.uuid4())}
 if options:req['postgreSql']=options
 if secret:req['postgreSqlSecrets']={'Password':secret}
 if tls:req['postgreSqlTls']=tls
 r=json.loads(execs(binary,'helper-stdin',input=json.dumps(req)).stdout)
 if r.get('outputBase64'):r['state']=json.loads(base64.b64decode(r['outputBase64']))
 return r
def state():return call()['state']
s=state();instance=s['InstanceId'];port=s['Port'];name='rk'+instance.replace('-','');unit='postgresql@16-'+name+'.service'
if not s['Running']:
 check(call({'Confirmed':True,'Command':{'Action':'Start','InstanceId':instance,'ExpectedRevision':s['Revision'],'Port':port}})['success'],'fixture explicitly starts local instance')
pw=secrets.token_hex(20);suffix=secrets.token_hex(3);db='dbremote'+suffix;user='user'+suffix
for action,account,permission in [('CreateDatabase',None,None),('CreateAccount',user,'ReadWrite')]:
 cmd={'Action':action,'InstanceId':instance,'ExpectedRevision':state()['Revision'],'Database':db,'Account':account,'Permission':permission,'CredentialReference':str(uuid.uuid4()) if account else None}
 check(call({'Confirmed':True,'AccountCommand':cmd},pw if account else None)['success'],action+' creates owned fixture resource')
execs('mkdir','-p','/tmp/db05-tls')
execs('openssl','req','-x509','-newkey','rsa:2048','-nodes','-keyout','/tmp/db05-tls/server.key','-out','/tmp/db05-tls/server.pem','-days','3','-subj','/CN=db05.test','-addext','subjectAltName=DNS:db05.test','-addext','extendedKeyUsage=serverAuth')
cert=execs('cat','/tmp/db05-tls/server.pem').stdout;key=execs('cat','/tmp/db05-tls/server.key').stdout
der=base64.b64decode(''.join(cert.splitlines()[1:-1]));fingerprint=hashlib.sha256(der).hexdigest().upper();material={'CertificatePem':cert,'PrivateKeyPem':key}
for c in [allowed,denied]:
 run(['docker','exec','-i',c,'sh','-c','umask 077; cat > /tmp/db05-ca.pem'],cert)
 run(['docker','exec','-i',c,'sh','-c','umask 077; cat > /tmp/db05.pgpass'],f'*:{port}:*:{user}:{pw}\n')
 run(['docker','exec','-i',c,'sh','-c','umask 077; cat > /tmp/db05-wrong.pgpass'],f'*:{port}:*:{user}:invalid-probe-password\n')
def probe(c,ssl='verify-full',host='db05.test',password=True,database=None):
 env=['-e','PGPASSFILE=/tmp/db05.pgpass'] if password else ['-e','PGPASSFILE=/tmp/db05-wrong.pgpass']
 p=run(['docker','exec',*env,c,'psql','-XqAtw','-v','ON_ERROR_STOP=1',f'host={host} hostaddr={ips[server]} port={port} user={user} dbname={database or db} connect_timeout=3 sslmode={ssl} sslrootcert=/tmp/db05-ca.pem','-c','SELECT ssl FROM pg_stat_ssl WHERE pid=pg_backend_pid()'],ok=False)
 return p.returncode==0 and (ssl=='disable' or p.stdout.strip()=='t')
def command(enabled=True):
 return {'InstanceId':instance,'ExpectedRevision':state()['Revision'],'Enabled':enabled,**({'ListenAddress':ips[server],'ServerName':'db05.test','Database':db,'Account':user,'Sources':[ips[allowed]+('/128' if '--ipv6' in sys.argv else '/32')],'CertificateId':str(uuid.uuid4()),'CertificateFingerprint':fingerprint} if enabled else {})}
# An unrelated owned-by-fixture rule must survive every transition.
execs('nft','-f','-',input='add table inet db05_unrelated\nadd chain inet db05_unrelated untouched { type filter hook input priority 10; policy accept; }\n')
unrelated=execs('nft','-s','list','table','inet','db05_unrelated').stdout
bad=command();bad['CertificateFingerprint']='0'*64
check(not call({'Confirmed':True,'RemoteAccess':bad},tls=material)['success'] and state()['PendingOperationId'] is None,'certificate mismatch refused before mutation')
check(not probe(allowed),'default local-only listener refuses external source')
options={'Confirmed':True,'RemoteAccess':command()};op=str(uuid.uuid4())
if '--fault' in sys.argv:
 watcher="import pathlib,time,os; p=pathlib.Path('/etc/postgresql/16/"+name+"/rk-server.key'); end=time.monotonic()+30\nwhile time.monotonic()<end:\n if p.exists():\n  os.chmod(p,0);break\n time.sleep(.001)\nelse:raise RuntimeError('fault timeout')"
 fault=subprocess.Popen(['docker','exec',server,'python3','-c',watcher],stdout=subprocess.PIPE,stderr=subprocess.PIPE)
 import time;time.sleep(.2)
 r=call(options,tls=material,id=op);fault.wait(timeout=35)
 check(not r['success'] and state()['PendingRemoteAccess'] is not None,'TLS activation failure retains original pending operation')
 check(not probe(allowed) and not state()['Running'],'activation failure closes network and stops server')
 r=call({'Confirmed':True,'RecoverOperationId':op},tls=material,recover=True)
 check(r['success'] and r['state']['PendingOperationId'] is None,'same operation recovers after transient key permission failure')
else:r=call(options,tls=material,id=op)
if not r['success']:print('REMOTE_FAILURE',r.get('error'),state().get('ProblemCode'),flush=True)
check(r['success'] and r['state']['CommittedOperationId']==op,'listener HBA TLS firewall transaction commits')
check(probe(allowed),'allowed external source authenticates with verify-full TLS')
check(not probe(denied),'denied external source cannot connect')
rules=json.loads(execs('nft','-j','list','table','inet','rkpg_'+instance.replace('-','')).stdout)['nftables']
check(any(e.get('counter',{}).get('packets',0)>0 for item in rules for e in item.get('rule',{}).get('expr',[])),'firewall counter proves denied packets reached the instance')
check(not probe(allowed,ssl='disable'),'allowed source cannot use plaintext')
check(not probe(allowed,host='wrong.db05.test'),'wrong server name is rejected')
check(not probe(allowed,password=False),'wrong password is rejected')
check(not probe(allowed,database='postgres'),'remote account cannot access another database')
check(call(options,tls=material,id=op)['success'],'same remote operation is idempotent')
check(execs('nft','-s','list','table','inet','db05_unrelated').stdout==unrelated,'unrelated firewall table unchanged')
# Restart exercises persistent guard before the listener starts, including absent kernel table.
execs('systemctl','stop',unit);execs('nft','delete','table','inet','rkpg_'+instance.replace('-',''))
execs('systemctl','start',unit)
check(probe(allowed) and not probe(denied),'systemd restart restores firewall before opening listener')
session=subprocess.Popen(['docker','exec','-e','PGPASSFILE=/tmp/db05.pgpass',allowed,'psql','-XqAtw',f'host=db05.test hostaddr={ips[server]} port={port} user={user} dbname={db} sslmode=verify-full sslrootcert=/tmp/db05-ca.pem','-c','SELECT pg_sleep(30)'],stdout=subprocess.PIPE,stderr=subprocess.PIPE)
import time;time.sleep(1)
check(session.poll() is None,'external session is established before close')
r=call({'Confirmed':True,'RemoteAccess':command(False)})
check(session.wait(timeout=10)!=0,'closing terminates existing external session')
check(r['success'] and r['state']['RemoteAccess'] is None and not r['state']['Running'],'close restores local configuration and keeps instance stopped')
check(not probe(allowed) and not probe(denied),'closing blocks allowed and denied external sources')
check(execs('nft','-s','list','table','inet','db05_unrelated').stdout==unrelated,'closing preserves unrelated firewall table')
check(execs('test','!','-e','/etc/postgresql/16/'+name+'/rk-server.key',ok=False).returncode==0,'closing removes owned TLS private key')
check('PrivateKeyPem' not in execs('cat','/var/lib/relaxkonos/postgresql-control/state.json').stdout,'Helper ledger never stores TLS key material')
for c in [allowed,denied]:run(['docker','exec',c,'rm','-f','/tmp/db05.pgpass','/tmp/db05-wrong.pgpass','/tmp/db05-ca.pem'])
print('DB-05 native remote checks passed: '+str(checks),flush=True)
