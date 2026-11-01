#!/usr/bin/env python3
"""Root-only, payload-free audit assertions for the fixed Linux v2 acceptance instance."""
import json
import os
import pathlib
import pwd
import sqlite3

assert os.geteuid() == 0
root = pathlib.Path('/var/lib/relaxkonos-v2-test/server')
document = json.loads((root/'module-management/privilege-plans.json').read_text())
assert document['schemaVersion'] == 3
plans = document['plans']
assert any(plan['state'] == 'RecoveryRequired' for plan in plans)
assert any(plan['state'] == 'Succeeded' for plan in plans)
administrator = 'linux:' + str(pwd.getpwnam('nanami').pw_uid)
attempts = [plan for plan in plans if plan['state'] in ('RecoveryRequired', 'Succeeded')]
assert attempts
with sqlite3.connect('file:' + str(root/'audit.db') + '?mode=ro', uri=True) as database:
    for plan in attempts:
        audit = plan['audit']
        assert audit['initiatingUser'] and audit['approvingUser'] == audit['initiatingUser']
        method = 'standing-consent:linux-pam-administrator' if plan['session'].startswith('consent:') else 'linux-pam-administrator'
        assert audit['administrator'] == dict(identity=administrator, authenticationMethod=method)
        if plan['session'].startswith('consent:'):
            consents = json.loads((root/'module-management/privilege-consents.json').read_text())['consents']
            consent = next(c for c in consents if c['id'].replace('-', '') == plan['session'].removeprefix('consent:'))
            assert consent['actor'] == audit['initiatingUser'] and consent['approvedAt']
            assert consent['administrator'] == dict(identity=administrator, authenticationMethod='linux-pam-administrator')
        assert audit['confirmedAt']
        # A process killed in flight cannot observe completion or write a terminal
        # Helper audit event. Recovery must preserve that uncertainty, not invent it.
        interrupted = audit['attemptCompletedAt'] is None
        if interrupted:
            assert plan['state'] == 'RecoveryRequired' and plan['updated']
        count, = database.execute('SELECT COUNT(*) FROM security_audit_entries WHERE operation_id=? '
            'AND actor_reference IS NOT NULL AND actor_reference<>\'\'', (plan['id'],)).fetchone()
        assert count >= (1 if interrupted else 2), 'Each attempted plan must correlate to its surviving durable Helper actor records'

def no_credentials(value):
    if isinstance(value, dict):
        assert not set(value) & {'password', 'confirmationToken', 'serviceToken', 'accessToken'}
        for child in value.values():
            no_credentials(child)
    elif isinstance(value, list):
        for child in value:
            no_credentials(child)

no_credentials(document)
print('PASS Linux schema 3 retains actual PAM administrator identity, initiating/approving users and timestamps across Core restart')
print('PASS Linux unknown and successful attempts correlate to durable actor-attributed Helper audit records by original plan ID')
print('PASS Linux persisted privilege plans contain no credential or plaintext confirmation-token fields')
