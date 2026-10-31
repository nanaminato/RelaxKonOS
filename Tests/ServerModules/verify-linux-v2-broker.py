#!/usr/bin/env python3
"""Real PAM + signed third-party installation + ModuleHost + Broker + Helper acceptance.

Run as the SSH test operator after prepare-linux-v2-broker-fixture.py. Password is read with getpass,
kept in memory, passed only to sudo/PAM/Core, never printed or written to artifacts.
"""
import getpass
import json
import pathlib
import re
import subprocess
import time
import urllib.error
import urllib.request
import uuid

base = 'http://127.0.0.1:16591'
root = '/opt/relaxkonos-v2-test'
fixtures = pathlib.Path.home()/'modulehost-v2-test'
password = getpass.getpass('Test operator / PAM administrator password: ')
subprocess.run(['sudo','-S','-p','','-v'],input=password+'\n',text=True,check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
token = None

def privileged(*arguments):
    return subprocess.run(['sudo','-n',*arguments],check=True,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,text=True).stdout

def api(method, path, body=None, binary=None, expected=200):
    headers = {}
    if token:
        headers['Authorization'] = 'Bearer '+token
    data = None
    if body is not None:
        data = json.dumps(body).encode()
        headers['Content-Type'] = 'application/json'
    if binary is not None:
        data = binary
        headers['Content-Type'] = 'application/octet-stream'
    request = urllib.request.Request(base+path,data=data,headers=headers,method=method)
    try:
        with urllib.request.urlopen(request,timeout=45) as response:
            status = response.status
            content = response.read()
    except urllib.error.HTTPError as error:
        status, content = error.code, error.read()
    allowed = {expected} if isinstance(expected,int) else expected
    if status not in allowed:
        try:
            code = json.loads(content).get('problemCode','unspecified')
        except (ValueError,AttributeError):
            code = 'unspecified'
        if not re.fullmatch('[A-Za-z0-9._-]{1,128}',str(code)):
            code = 'unspecified'
        raise AssertionError('HTTP '+str(status)+' '+str(code)+' at '+path)
    try:
        return json.loads(content)
    except ValueError:
        return content.decode()

def wait_ready():
    for _ in range(30):
        try:
            with urllib.request.urlopen(base+'/healthz',timeout=2) as response:
                if response.status == 200:
                    return
        except (OSError,urllib.error.URLError):
            pass
        time.sleep(1)
    raise AssertionError('Acceptance Server did not become healthy')

def restart():
    privileged('systemctl','restart','relaxkonos-v2-test.service')
    wait_ready()

def login():
    global token
    token = None
    result = api('POST','/api/v1.0/auth/login',dict(identifier='nanami',password=password,
        clientPlatform='linux',deviceName='ModuleHost v2 acceptance',clientVersion='0.2.2'))
    token = result['tokens']['accessToken']
    return result['user']['id']

def pid():
    return privileged('systemctl','show','relaxkonos-module-broker-fixture.service','--property=MainPID','--value').strip()

wait_ready()
user_id = login()
print('PASS real PAM login through the private Helper')
privileged('systemctl','stop','relaxkonos-v2-test.service')
privileged('python3','-c',
    'import json,pathlib,sys; p=pathlib.Path("/opt/relaxkonos-v2-test/appsettings.host.json"); d=json.loads(p.read_text()); d["ModuleManagement"]["Administrators"]=[sys.argv[1]]; p.write_text(json.dumps(d))',user_id)
identity = json.loads(privileged(root+'/RelaxKonOS.Server','modules','diagnose','--content-root',root))
privileged(root+'/RelaxKonOS.Server','modules','accept-catalog','--content-root',root,'--source-id','v2-test',
    '--catalog',str(fixtures/'package/catalog.json'),'--service-id',identity['serviceId'],'--expected-revision','uninitialized',
    '--idempotency-key','v2-real-acceptance')
privileged('systemctl','start','relaxkonos-v2-test.service')
wait_ready()
login()
package_result = json.loads((fixtures/'package-result.json').read_text())
upload = api('POST','/api/v1.0/server/module-packages/upload',binary=pathlib.Path(package_result['archivePath']).read_bytes())
plan = api('POST','/api/v1.0/server/module-packages/plans',dict(sourceId='v2-test',releaseId='fixture-http-1',uploadId=upload['uploadId']))
assert plan['runtime'] == 'module-host' and plan['privileges'][0]['target'] == 'relaxkonos-module-broker-fixture.service'
api('POST','/api/v1.0/server/module-packages/plans/'+plan['planId']+'/apply')
print('PASS signed schemaVersion=2 upload, explicit privilege preview, confirmed install and exact code trust')
restart()
login()
inventory = api('GET','/api/v1.0/server/modules')
fact = next(item for item in inventory if item['id'] == 'fixture-http')
desired = sorted({item['id'] for item in inventory if item['desiredEnabled']} | {'fixture-http'})
enable = api('POST','/api/v1.0/server/module-plans',dict(serviceId=fact['serviceId'],action='Enable',
    moduleIds=['fixture-http'],enabledModuleIds=desired,expectedRevision=fact['desiredRevision'],idempotencyKey=str(uuid.uuid4())))
assert not enable['blockedBy']
api('POST','/api/v1.0/server/module-plans/'+enable['planId']+'/apply')
restart()
login()
for _ in range(30):
    hosts = api('GET','/api/v1.0/server/module-hosts')['hosts']
    host = next((item for item in hosts if item['moduleId'] == 'fixture-http'),None)
    if host and host['status'] == 'running':
        break
    if host and host['status'] == 'faulted':
        raise AssertionError('ModuleHost faulted: '+str(host.get('problemCode')))
    time.sleep(1)
else:
    raise AssertionError('ModuleHost did not reach running')
assert api('GET','/api/v1.0/server/module-hosts/fixture-http/hello') == 'hello from module host'
print('PASS enabled signed third-party module starts under dedicated identity and authenticates via Server proxy')

def prepare():
    result = api('POST','/api/v1.0/server/module-hosts/fixture-http/privilege',dict(kind='service-restart',
        target='relaxkonos-module-broker-fixture.service',reason='Restart only the fixed acceptance service',idempotencyKey=str(uuid.uuid4())))
    assert result['state'] == 'AwaitingConfirmation'
    return api('GET','/api/v1.0/server/module-privileges/plans/'+result['planId'])

before = pid()
privilege = prepare()
approval = dict(planHash=privilege['planHash'],confirmationToken=privilege['confirmationToken'],administratorUsername='nanami',password='invalid-fixture-password')
api('POST','/api/v1.0/server/module-privileges/plans/'+privilege['planId']+'/approve',approval,expected=403)
assert pid() == before
print('PASS a bad administrator password cannot execute the prepared action')
approval['password'] = password
result = api('POST','/api/v1.0/server/module-privileges/plans/'+privilege['planId']+'/approve',approval)
assert result['state'] == 'Succeeded'
after = pid()
assert after != '0' and after != before
api('POST','/api/v1.0/server/module-privileges/plans/'+privilege['planId']+'/approve',approval,expected=409)
assert pid() == after
print('PASS Core confirmation executes one real Helper restart and rejects replay')
second = prepare()
assert pid() == after
result = api('POST','/api/v1.0/server/module-privileges/plans/'+second['planId']+'/approve',
    dict(planHash=second['planHash'],confirmationToken=second['confirmationToken']))
assert result['state'] == 'Succeeded' and pid() != after
print('PASS remembered administrator verification still requires a new explicit plan confirmation')
pending = prepare()
installed = api('GET','/api/v1.0/server/module-packages/installed')['items'][0]
api('POST','/api/v1.0/server/module-trust/fixture-http',dict(artifactSha256=installed['artifactSha256'],
    manifestSha256=installed['manifestSha256'],trusted=False))
api('GET','/api/v1.0/server/module-hosts/fixture-http/hello',expected=403)
api('POST','/api/v1.0/server/module-privileges/plans/'+pending['planId']+'/approve',
    dict(planHash=pending['planHash'],confirmationToken=pending['confirmationToken']),expected={403,409})
print('PASS exact code trust revocation immediately blocks proxy access and pending privilege execution')
print('Linux v2 signed-install / dedicated-host / Broker / PAM / Helper acceptance passed.')
