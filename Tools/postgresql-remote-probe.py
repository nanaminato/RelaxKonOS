"""External PostgreSQL TLS probe. Read a JSON request from stdin; never supply passwords in argv.
Required: host, hostAddress, port, database, account, caFile, password, expected (allowed|denied|closed).
Run on the actual source host with psql installed. Output contains outcomes, never credentials.
An unavailable target is not proof of HBA/firewall correctness; denied/closed results are connectivity evidence only.
"""
import json,os,subprocess,sys,tempfile
from pathlib import Path

def main():
 r=json.load(sys.stdin)
 if r.get('expected') not in ['allowed','denied','closed']:raise ValueError('invalid expectation')
 for k in ['host','hostAddress','database','account','caFile']:
  if not isinstance(r.get(k),str) or not r[k] or any(c in r[k] for c in ['\n','\r','\0']):raise ValueError('invalid input')
 if not isinstance(r.get('port'),int) or not 1<=r['port']<=65535:raise ValueError('invalid port')
 def quote(value):return "'"+str(value).replace('\\','\\\\').replace("'","\\'")+"'"
 def escape(value):return str(value).replace('\\','\\\\').replace(':','\\:')
 with tempfile.TemporaryDirectory(prefix='rk-postgresql-probe-') as directory:
  path=Path(directory)/'password';path.touch(mode=0o600)
  def probe(ssl='verify-full',name=None,password=None):
   secret=r['password'] if password is None else password
   if not isinstance(secret,str) or any(c in secret for c in ['\n','\r','\0']):raise ValueError('invalid credential')
   path.write_text(':'.join(escape(x) for x in ['*',r['port'],'*',r['account'],secret])+'\n',encoding='utf-8')
   env=os.environ.copy();env.pop('PGPASSWORD',None);env['PGPASSFILE']=str(path)
   connection=' '.join(k+'='+quote(v) for k,v in {'host':name or r['host'],'hostaddr':r['hostAddress'],'port':r['port'],'dbname':r['database'],'user':r['account'],'sslmode':ssl,'sslrootcert':r['caFile'],'connect_timeout':5}.items())
   result=subprocess.run(['psql','-XqAtw','-v','ON_ERROR_STOP=1',connection,'-c','SELECT ssl FROM pg_stat_ssl WHERE pid=pg_backend_pid()'],env=env,capture_output=True,timeout=12)
   return result.returncode==0 and (ssl=='disable' or result.stdout.strip()==b't')
  connected=probe();result={'expected':r['expected'],'tlsAuthenticated':connected,'externalEvidence':True}
  if r['expected']=='allowed':
   result.update(plaintextRejected=not probe('disable'),wrongNameRejected=not probe(name='invalid-postgresql-probe.invalid'),wrongPasswordRejected=not probe(password='deliberately-invalid-probe-password'))
   result['passed']=connected and all(result[k] for k in ['plaintextRejected','wrongNameRejected','wrongPasswordRejected'])
  else:result['passed']=not connected;result['limitation']='Connection failure alone does not identify which network or authentication layer rejected it.'
  print(json.dumps(result));return 0 if result['passed'] else 1
if __name__=='__main__':
 try:sys.exit(main())
 except Exception:print(json.dumps({'passed':False,'problem':'probe_unavailable_or_invalid_input'}));sys.exit(2)
