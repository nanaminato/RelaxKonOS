#!/usr/bin/env python3
"""Classify only this task's new test-unit startup failure; never export journal text or fields."""
import subprocess

raw = subprocess.run(['journalctl','-u','relaxkonos-v2-test.service','-n','150','--no-pager','-o','cat'],
    check=True, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True).stdout
categories = {
    'permission-denied': ['UnauthorizedAccessException', 'Permission denied'],
    'invalid-operation': ['InvalidOperationException'],
    'sqlite-initialization': ['SqliteException', 'SQLite Error'],
    'missing-file-or-assembly': ['FileNotFoundException', 'Could not load file or assembly'],
    'invalid-argument': ['ArgumentException', 'ArgumentOutOfRangeException'],
    'socket-bind': ['AddressInUseException', 'Address already in use'],
    'invalid-json': ['JsonException'],
    'configuration': ['configuration is invalid', 'must be configured', 'ServerModeResolver'],
    'broker-endpoint-binding': ['ModulePrivilegeEndpoints', 'Body was inferred'],
    'dependency-resolution': ['Unable to resolve service', 'Cannot consume scoped service'],
    'module-management': ['ModuleManagementStore', 'module-package-'],
    'host-identity': ['AddHostIdentity', 'HostIdentity'],
    'application-started': ['Application started.'],
}
print('Known startup categories:', ', '.join(name for name, markers in categories.items() if any(x in raw for x in markers)) or 'unclassified')
