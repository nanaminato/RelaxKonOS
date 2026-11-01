#!/usr/bin/env python3
"""Real Helper interruption acceptance for the dedicated v2 test instance only."""
import json
import pathlib
import time
import uuid
from linux_v2_acceptance_support import api, fixtures, login, password, pid, privileged, restart, root, wait_ready

helper_service = 'relaxkonos-v2-test-helper.service'
privileged('systemctl', 'start', helper_service, 'relaxkonos-v2-test.service')
wait_ready()
login()
installed = next(item for item in api('GET', '/api/v1.0/server/module-packages/installed')['items'] if item['moduleId'] == 'fixture-http')

def trust(value):
    api('POST', '/api/v1.0/server/module-trust/fixture-http', dict(
        artifactSha256=installed['artifactSha256'], manifestSha256=installed['manifestSha256'], trusted=value))

def prepare(key):
    prepared = api('POST', '/api/v1.0/server/module-hosts/fixture-http/privilege', dict(
        kind='service.manage', action='restart', target='relaxkonos-module-broker-fixture.service',
        reason='Dedicated Helper interruption acceptance', idempotencyKey=key))
    return api('GET', '/api/v1.0/server/module-privileges/plans/' + prepared['planId'])

def approve(plan, credentials=False, expected=200):
    body = dict(planHash=plan['planHash'], confirmationToken=plan['confirmationToken'])
    if credentials:
        body.update(administratorUsername='nanami', password=password)
    return api('POST', '/api/v1.0/server/module-privileges/plans/' + plan['planId'] + '/approve', body, expected=expected)

trust(True)
try:
    restart()
    login()
    for _ in range(30):
        hosts = api('GET', '/api/v1.0/server/module-hosts')['hosts']
        if any(host['moduleId'] == 'fixture-http' and host['status'] == 'running' for host in hosts):
            break
        time.sleep(1)
    else:
        raise AssertionError('Dedicated ModuleHost did not become ready')
    privileged('systemctl', 'start', 'relaxkonos-module-broker-fixture.service')
    initial = prepare(str(uuid.uuid4()))
    assert approve(initial, credentials=True)['state'] == 'Succeeded'
    print('PASS real administrator verification and Helper action establish the confirmation session')
    pending = prepare(str(uuid.uuid4()))
    before = pid()
    privileged('systemctl', 'stop', helper_service)
    try:
        result = approve(pending)
        assert result['state'] == 'RecoveryRequired', 'Unavailable Helper must leave an unknown result; got ' + result['state']
        assert pid() == before
        print('PASS stopped real Helper produces RecoveryRequired without executing the service action')
    finally:
        privileged('systemctl', 'start', helper_service)
    for _ in range(3):
        status = api('GET', '/api/v1.0/server/module-privileges/plans/' + pending['planId'])
        assert status['state'] == 'RecoveryRequired' and status['confirmationToken'] == ''
        assert pid() == before
        time.sleep(1)
    approve(pending, expected=409)
    assert pid() == before
    print('PASS Helper restart and repeated queries never replay the unknown action or reissue consent')
    fresh = prepare(str(uuid.uuid4()))
    assert pid() == before
    assert approve(fresh)['state'] == 'Succeeded' and pid() != before
    print('PASS a fresh explicitly confirmed plan works after Helper restoration')
finally:
    privileged('systemctl', 'start', helper_service)
    trust(False)
print('Linux real Helper interruption acceptance passed; module trust revoked after verification.')
