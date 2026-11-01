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

