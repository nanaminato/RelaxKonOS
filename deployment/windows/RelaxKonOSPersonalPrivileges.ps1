# Keep the deployment engine and runtime in the original user's session. Only this fixed Helper
# installer crosses UAC; another administrator's credentials never change the installation owner.
function Set-PersonalPrivileges([string] $InstallRoot, [string] $DataRoot, [string] $Version,
    [string] $Action = 'install', [string] $FileAccess = 'restricted', [string] $FileRootsFile,
    [string] $ListenUrl, [bool] $AddFirewallRule = $false) {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $request = [ordered]@{
        ownerSid = $sid; action = $Action; helperSource = (Join-Path $InstallRoot ('versions\' + $Version + '\privileged-helper'))
        fileAccess = $FileAccess; fileRoots = @(); listenUrl = $ListenUrl; addFirewallRule = $AddFirewallRule
    }
    if ($FileAccess -eq 'whitelist' -and $FileRootsFile) { $request.fileRoots = @(Get-Content -LiteralPath $FileRootsFile -Raw | ConvertFrom-Json) }
    $requestPath = Join-Path $DataRoot ('privilege-request-' + [guid]::NewGuid().ToString('N') + '.json')
    [IO.File]::WriteAllText($requestPath, ($request | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    # A retained installation may have an owner-only inherited DACL. Another administrator must
    # be able to read this fixed UAC request without taking ownership of the user's data.
    & icacls $requestPath /inheritance:r /grant:r ("*$sid" + ':F') '*S-1-5-18:F' '*S-1-5-32-544:R' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot protect the personal UAC request.' }
    try {
        $engine = Join-Path $PSScriptRoot 'Set-RelaxKonOSPersonalPrivileges.ps1'
        # Encode literal arguments as PowerShell strings; never interpolate them into shell syntax.
        $command = "& '" + $engine.Replace("'", "''") + "' -RequestPath '" + $requestPath.Replace("'", "''") + "'"
        $command = "try { $command; exit 0 } catch { [Console]::Error.WriteLine(`$_.Exception.Message); exit 1 }"
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        $powerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $process = Start-Process -FilePath $powerShell -Verb RunAs -WindowStyle Hidden -PassThru -Wait -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded)
        if ($process.ExitCode -ne 0) { throw 'Personal Helper installation failed or administrator authorization was cancelled.' }
    } finally { Remove-Item -LiteralPath $requestPath -Force -ErrorAction SilentlyContinue }
    if ($Action -eq 'install') {
        return (Get-Content -LiteralPath (Join-Path $env:ProgramFiles ('RelaxKonOS-Personal\' + $sid + '\helper.json')) -Raw | ConvertFrom-Json)
    }
}
