param(
    [Parameter(Mandatory)][string]$Server,
    [Parameter(Mandatory)][pscredential]$Credential,
    [Parameter(Mandatory)][string]$ReportPath
)
$ErrorActionPreference = 'Stop'
$rows = [Collections.Generic.List[object]]::new()
$headers = @{}
function Record([string]$Check, [string]$Result, [string]$Detail = '') {
    $rows.Add([pscustomobject]@{ check = $Check; result = $Result; detail = $Detail })
    Write-Output "$Check : $Result $Detail"
}
function Read([string]$Route) {
    Invoke-RestMethod -Uri ($Server.TrimEnd('/') + $Route) -Headers $headers -SkipCertificateCheck -TimeoutSec 15
}
function WritePreferences($Value) {
    Invoke-RestMethod -Uri ($Server.TrimEnd('/') + $script:prefRoute) -Headers $headers -Method Put `
        -Body ($Value | ConvertTo-Json -Depth 40) -ContentType 'application/json' -SkipCertificateCheck -TimeoutSec 15
}
try {
    $loginBody = @{ identifier = $Credential.UserName; password = $Credential.GetNetworkCredential().Password;
        clientPlatform = 'windows'; deviceName = 'Settings acceptance'; clientVersion = '1.0.0' }
    $login = Invoke-RestMethod -Uri ($Server.TrimEnd('/') + '/api/v1.0/auth/login') -Method Post `
        -Body ($loginBody | ConvertTo-Json) -ContentType 'application/json' -SkipCertificateCheck -TimeoutSec 15
    $loginBody = $null
    $headers.Authorization = 'Bearer ' + $login.tokens.accessToken
    Record 'login' 'PASS'
    $script:prefRoute = '/api/v1.0/workspaces/' + $login.workspace.id + '/preferences'
    foreach ($route in @('/api/v1.0/settings/catalog', '/api/v1.0/host-settings/network',
        '/api/v1.0/host-settings/time', '/api/v1.0/host-settings/identity', '/api/v1.0/docker/proxy',
        ('/api/v1.0/workspaces/' + $login.workspace.id + '/environment'))) {
        $response = Invoke-WebRequest -Uri ($Server.TrimEnd('/') + $route) -Headers $headers `
            -SkipCertificateCheck -SkipHttpErrorCheck -TimeoutSec 15
        Record $route $(if ($response.StatusCode -eq 200) { 'PASS' } else { 'UNAVAILABLE' }) ('HTTP ' + $response.StatusCode)
    }
    foreach ($kind in @('time-format', 'wallpaper', 'appearance')) {
        $original = Read $script:prefRoute
        $baseline = $original | ConvertTo-Json -Depth 40
        $draft = $baseline | ConvertFrom-Json
        switch ($kind) {
            'time-format' { $draft.timeFormat = if ($draft.timeFormat -eq '24h') { '12h' } else { '24h' } }
            'wallpaper' { $draft.wallpaperKey = if ($draft.wallpaperKey -eq 'builtin:ocean-waves') { 'builtin:alpine-lake' } else { 'builtin:ocean-waves' } }
            'appearance' { $draft.desktopExperience.appearance.mode = if ($draft.desktopExperience.appearance.mode -eq 'dark') { 'light' } else { 'dark' } }
        }
        $saved = $null
        try {
            $saved = WritePreferences $draft
            $readback = Read $script:prefRoute
            $matches = switch ($kind) {
                'time-format' { $readback.timeFormat -eq $draft.timeFormat }
                'wallpaper' { $readback.wallpaperKey -eq $draft.wallpaperKey }
                'appearance' { $readback.desktopExperience.appearance.mode -eq $draft.desktopExperience.appearance.mode }
            }
            Record "$kind/save-read" $(if ($matches) { 'PASS' } else { 'FAIL' })
            $deadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
            while ($readback.persistedRevision -ne $readback.revision -and [DateTimeOffset]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 300
                $readback = Read $script:prefRoute
            }
            Record "$kind/persistence" $(if ($readback.persistedRevision -eq $readback.revision) { 'PASS' } else { 'FAIL' })
            $stale = Invoke-WebRequest -Uri ($Server.TrimEnd('/') + $script:prefRoute) -Method Put -Headers $headers `
                -Body ($draft | ConvertTo-Json -Depth 40) -ContentType 'application/json' -SkipCertificateCheck -SkipHttpErrorCheck -TimeoutSec 15
            Record "$kind/stale-revision" $(if ($stale.StatusCode -eq 409) { 'PASS' } else { 'FAIL' }) ('HTTP ' + $stale.StatusCode)
        }
        catch { Record "$kind/write" 'FAIL' $_.Exception.GetType().Name }
        finally {
            if ($saved) {
                $current = Read $script:prefRoute
                if ($current.revision -eq $saved.revision) {
                    $restore = $baseline | ConvertFrom-Json
                    $restore.revision = $current.revision
                    $null = WritePreferences $restore
                    $restored = Read $script:prefRoute
                    $verified = switch ($kind) {
                        'time-format' { $restored.timeFormat -eq $original.timeFormat }
                        'wallpaper' { $restored.wallpaperKey -eq $original.wallpaperKey }
                        'appearance' { $restored.desktopExperience.appearance.mode -eq $original.desktopExperience.appearance.mode }
                    }
                    Record "$kind/restore" $(if ($verified) { 'PASS' } else { 'FAIL' })
                }
                else { Record "$kind/restore" 'BLOCKED' 'Concurrent edit detected; baseline not overwritten.' }
            }
        }
    }
}
catch { Record 'acceptance' 'FAIL' $_.Exception.GetType().Name }
finally {
    if ($headers.Authorization) {
        try { $null = Invoke-WebRequest -Uri ($Server.TrimEnd('/') + '/api/v1.0/auth/logout') -Headers $headers `
            -Method Post -SkipCertificateCheck -SkipHttpErrorCheck -TimeoutSec 10 } catch { }
    }
    $headers.Clear(); $login = $null; $loginBody = $null
    [pscustomobject]@{ server = $Server; testedAt = [DateTimeOffset]::UtcNow; checks = $rows } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath -Encoding utf8
}
