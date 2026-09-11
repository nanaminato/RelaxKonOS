#!/usr/bin/env python3
"""Verify how the Windows share APIs treat SHARE_INFO_502 fields.

RelaxKonOS re-points a managed SMB share by deleting and re-adding it instead of calling
NetShareSetInfo. That is not a stylistic choice: NetShareSetInfo accepts SHARE_INFO_502 and
returns ERROR_SUCCESS while silently ignoring shi502_path, so an in-place update leaves the share
serving its previous directory. The Helper's post-apply health check then fails, and the operation
is rolled back. This probe proves the behaviour on the host actually running the server.

It creates one uniquely named scratch share, never touches an existing share, and deletes the
scratch share in a finally block. Run it as an administrator on the Windows host:

    python Tools/verify-windows-share-path.py
"""

import ctypes
import ctypes.wintypes as wt
import os
import sys
import tempfile
import uuid

netapi = ctypes.WinDLL("Netapi32.dll")
advapi = ctypes.WinDLL("Advapi32.dll")
kernel = ctypes.WinDLL("Kernel32.dll")


class SHARE_INFO_502(ctypes.Structure):
    _fields_ = [
        ("netname", ctypes.c_wchar_p),
        ("type", ctypes.c_uint32),
        ("remark", ctypes.c_wchar_p),
        ("permissions", ctypes.c_uint32),
        ("max_uses", ctypes.c_uint32),
        ("current_uses", ctypes.c_uint32),
        ("path", ctypes.c_wchar_p),
        ("passwd", ctypes.c_wchar_p),
        ("reserved", ctypes.c_uint32),
        ("security_descriptor", ctypes.c_void_p),
    ]


# Native x64 SHARE_INFO_502 offsets. A wrong layout would shift every pointer after the DWORDs,
# which is exactly the bug the Helper's own struct comment warns about.
EXPECTED_OFFSETS = [0, 8, 16, 24, 28, 32, 40, 48, 56, 64]

netapi.NetShareAdd.argtypes = [ctypes.c_wchar_p, wt.DWORD, ctypes.c_void_p, ctypes.POINTER(wt.DWORD)]
netapi.NetShareAdd.restype = wt.DWORD
netapi.NetShareSetInfo.argtypes = [ctypes.c_wchar_p, ctypes.c_wchar_p, wt.DWORD, ctypes.c_void_p, ctypes.POINTER(wt.DWORD)]
netapi.NetShareSetInfo.restype = wt.DWORD
netapi.NetShareGetInfo.argtypes = [ctypes.c_wchar_p, ctypes.c_wchar_p, wt.DWORD, ctypes.POINTER(ctypes.c_void_p)]
netapi.NetShareGetInfo.restype = wt.DWORD
netapi.NetShareDel.argtypes = [ctypes.c_wchar_p, ctypes.c_wchar_p, wt.DWORD]
netapi.NetShareDel.restype = wt.DWORD
netapi.NetApiBufferFree.argtypes = [ctypes.c_void_p]
advapi.ConvertStringSecurityDescriptorToSecurityDescriptorW.argtypes = [
    ctypes.c_wchar_p, wt.DWORD, ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(wt.DWORD)]
advapi.ConvertSecurityDescriptorToStringSecurityDescriptorW.argtypes = [
    ctypes.c_void_p, wt.DWORD, wt.DWORD, ctypes.POINTER(ctypes.c_wchar_p), ctypes.POINTER(wt.DWORD)]
kernel.LocalFree.argtypes = [ctypes.c_void_p]
kernel.LocalFree.restype = ctypes.c_void_p

SDDL_REVISION_1 = 1
DACL_SECURITY_INFORMATION = 0x00000004
READ_FOR_EVERYONE = "D:(A;;0x1200a9;;;WD)"
WIDER_ACL = "D:(A;;0x1200a9;;;WD)(A;;0x1200a9;;;BA)"

failures = []


def check(condition, message):
    print(("PASS" if condition else "FAIL") + ": " + message)
    if not condition:
        failures.append(message)


def to_descriptor(sddl):
    descriptor = ctypes.c_void_p()
    size = wt.DWORD()
    if not advapi.ConvertStringSecurityDescriptorToSecurityDescriptorW(
            sddl, SDDL_REVISION_1, ctypes.byref(descriptor), ctypes.byref(size)):
        raise OSError("ConvertStringSecurityDescriptor failed for " + sddl)
    return descriptor.value


def to_sddl(descriptor):
    text = ctypes.c_wchar_p()
    size = wt.DWORD()
    if not advapi.ConvertSecurityDescriptorToStringSecurityDescriptorW(
            descriptor, SDDL_REVISION_1, DACL_SECURITY_INFORMATION, ctypes.byref(text), ctypes.byref(size)):
        return "<unreadable>"
    try:
        return text.value
    finally:
        kernel.LocalFree(text)


def share_info(netname, path, remark, descriptor):
    info = SHARE_INFO_502()
    info.netname = netname
    info.type = 0
    info.remark = remark
    info.permissions = 0
    info.max_uses = 0xFFFFFFFF
    info.current_uses = 0
    info.path = path
    info.passwd = None
    info.reserved = 0
    info.security_descriptor = descriptor
    return info


def read_share(netname):
    buffer = ctypes.c_void_p()
    if netapi.NetShareGetInfo(None, netname, 502, ctypes.byref(buffer)) != 0:
        return None
    try:
        info = ctypes.cast(buffer, ctypes.POINTER(SHARE_INFO_502)).contents
        return info.path, info.remark, to_sddl(info.security_descriptor)
    finally:
        netapi.NetApiBufferFree(buffer)


def main():
    if os.name != "nt":
        sys.exit("This probe only runs on Windows.")
    offsets = [getattr(SHARE_INFO_502, name).offset for name, _ in SHARE_INFO_502._fields_]
    check(offsets == EXPECTED_OFFSETS and ctypes.sizeof(SHARE_INFO_502) == 72,
          "SHARE_INFO_502 matches the native x64 layout")

    netname = "rkdiag-" + uuid.uuid4().hex[:8]
    directory_a = tempfile.mkdtemp(prefix="rkdiag-a-")
    directory_b = tempfile.mkdtemp(prefix="rkdiag-b-")
    acl_read = to_descriptor(READ_FOR_EVERYONE)
    acl_wider = to_descriptor(WIDER_ACL)
    parameter_error = wt.DWORD()
    print("scratch share: " + netname + " (deleted again at the end)")
    try:
        added = netapi.NetShareAdd(None, 502, ctypes.byref(share_info(netname, directory_a, "remark-a", acl_read)), ctypes.byref(parameter_error))
        check(added == 0, "NetShareAdd creates the scratch share")
        if added != 0:
            return

        read_back = read_share(netname)
        check(read_back[0].lower() == directory_a.lower(), "the new share serves the requested directory")

        setinfo = netapi.NetShareSetInfo(None, netname, 502, ctypes.byref(share_info(netname, directory_b, "remark-b", acl_wider)), ctypes.byref(parameter_error))
        read_back = read_share(netname)
        check(setinfo == 0, "NetShareSetInfo reports success")
        check(read_back[1] == "remark-b", "NetShareSetInfo applies the remark")
        check(read_back[2] != to_sddl(acl_read), "NetShareSetInfo applies the security descriptor")
        check(read_back[0].lower() != directory_b.lower(),
              "NetShareSetInfo silently ignores the path, which is why a re-point needs a recreation")

        netapi.NetShareDel(None, netname, 0)
        recreated = netapi.NetShareAdd(None, 502, ctypes.byref(share_info(netname, directory_b, "remark-b", acl_wider)), ctypes.byref(parameter_error))
        read_back = read_share(netname)
        check(recreated == 0, "NetShareAdd re-creates the share")
        check(read_back[0].lower() == directory_b.lower(), "the recreated share serves the new directory")
    finally:
        netapi.NetShareDel(None, netname, 0)
        kernel.LocalFree(acl_read)
        kernel.LocalFree(acl_wider)
        for directory in (directory_a, directory_b):
            try:
                os.rmdir(directory)
            except OSError as error:
                print("cleanup: " + str(error))

    print("")
    if failures:
        print(str(len(failures)) + " check(s) failed on this host.")
        sys.exit(1)
    print("All checks passed: a Windows share path change requires delete + add.")


if __name__ == "__main__":
    main()
