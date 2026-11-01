$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../deployment/windows/RelaxKonOSModuleHostRuntime.ps1')
$previous = @'
{"ModuleManagement":{"Authority":"managed","Administrators":["admin"]},"ModulePackages":{"Sources":{"test":{"publicKeysSpki":{"key":"public-key"},"authorizedModuleIds":["fixture"],"enabled":true}}},"ModulePrivileges":{"Services":["fixed-service"]},"ModuleHosts":{"ProbeIntervalSeconds":2},"ModuleHealth":{"IntervalSeconds":3},"ModuleResidency":{"Strategy":"Resident"},"Modules":{"fixture":true},"PrivilegedHelper":{"SharedSecret":"old-test-secret"},"Storage":{"DatabasePath":"old-path"}}
'@ | ConvertFrom-Json
foreach ($mode in @('system', 'personal')) {
    $settings = [ordered]@{ Server = @{ Mode = $mode }; Storage = @{ DatabasePath = 'new-path' }; PrivilegedHelper = @{ SharedSecret = 'new-test-secret' } }
    $json = ConvertTo-RelaxKonOSModuleHostConfigurationJson $settings $previous
    $actual = $json | ConvertFrom-Json
    foreach ($section in @('ModuleManagement', 'ModulePackages', 'ModulePrivileges', 'ModuleHosts', 'ModuleHealth', 'ModuleResidency', 'Modules')) {
        if (($actual.$section | ConvertTo-Json -Depth 32 -Compress) -ne ($previous.$section | ConvertTo-Json -Depth 32 -Compress)) { throw "Policy lost: $mode/$section" }
    }
    if ($actual.Storage.DatabasePath -ne 'new-path' -or $actual.PrivilegedHelper.SharedSecret -ne 'new-test-secret') { throw 'Installer-owned settings were overwritten.' }
    Write-Output "PASS $mode module policy survives serialization; new paths/secrets retained"
}
$fresh = ConvertTo-RelaxKonOSModuleHostConfigurationJson ([ordered]@{ Server = @{ Mode = 'system' } }) $null | ConvertFrom-Json
if ($fresh.PSObject.Properties['ModulePackages']) { throw 'Fresh installation invented source policy.' }
Write-Output 'PASS fresh installation has no invented module policy'
