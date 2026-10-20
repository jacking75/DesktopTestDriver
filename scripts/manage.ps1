param(
    [Parameter(Mandatory=$true)][ValidateSet('Initialize','Register','Start','Status','Stop','Restart','Backup','Restore','InstallLogonTask','RemoveLogonTask')][string]$Action,
    [ValidateSet('Controller','Agent')][string]$Role,
    [string]$PackageRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$Configuration, [string]$StateDirectory,
    [string]$MachineId, [string]$HostAddress = '127.0.0.1', [int]$Port = 32550,
    [string]$Certificate, [string]$BackupDirectory
)
$ErrorActionPreference = 'Stop'
if (-not $Role) {
    if (Test-Path -LiteralPath (Join-Path $PackageRoot 'Controller.exe')) { $Role='Controller' }
    elseif (Test-Path -LiteralPath (Join-Path $PackageRoot 'Agent.exe')) { $Role='Agent' }
    else { throw 'Role or a published package root is required.' }
}
$exe = Join-Path $PackageRoot ($Role+'.exe')
if (-not $Configuration) { $Configuration=Join-Path $PackageRoot ($Role+'.json') }
$Configuration=[IO.Path]::GetFullPath($Configuration)
if (-not (Test-Path -LiteralPath $exe)) { throw "Executable not found: $exe" }
if (-not ((& dotnet --list-runtimes) -match '^Microsoft.WindowsDesktop.App 10\.')) { throw '.NET 10 Desktop Runtime x64 is required.' }
function Invoke-Role([string[]]$Arguments) {
    & $exe --config $Configuration @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Role returned exit code $LASTEXITCODE" }
}
function Current-Identity {
    if (-not (Test-Path -LiteralPath $Configuration)) { return $null }
    $config=Get-Content -LiteralPath $Configuration -Raw -Encoding UTF8 | ConvertFrom-Json
    $identityFile=Join-Path $config.stateDirectory ($Role.ToLowerInvariant()+'-process.json')
    if (-not (Test-Path -LiteralPath $identityFile)) { return $null }
    $identity=Get-Content -LiteralPath $identityFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $process=Get-Process -Id $identity.pid -ErrorAction SilentlyContinue
    if (-not $process) { return $null }
    try {
        # A process may exit between Get-Process and these property reads. Keep one stable snapshot.
        $processPath=$process.Path; $processStarted=$process.StartTime
        if (-not $processPath -or -not $processStarted -or $process.HasExited) { return $null }
        if ($processPath -ne $identity.imagePath -or $processStarted.ToUniversalTime() -ne ([DateTimeOffset]$identity.createdAt).UtcDateTime) { return $null }
        if ((Get-FileHash -LiteralPath $processPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $identity.imageHash) { return $null }
    } catch { return $null }
    return $identity
}
function Stop-Role {
    if (-not (Current-Identity)) { return }
    if ($Role -eq 'Controller') { Invoke-Role -Arguments @('--shutdown') } else { Invoke-Role -Arguments @('--stop') }
    $limit=[Diagnostics.Stopwatch]::StartNew()
    while ((Current-Identity) -and $limit.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 100 }
    if (Current-Identity) { throw 'Graceful stop did not finish; input/worker stop remains unconfirmed.' }
}
function Start-Role {
    if (Current-Identity) { throw "$Role is already running." }
    # The Agent must be started from this user's logged-in desktop, or the Interactive logon task.
    $config=Get-Content -LiteralPath $Configuration -Raw -Encoding UTF8 | ConvertFrom-Json
    $out=Join-Path $config.stateDirectory 'host.stdout.log'; $err=Join-Path $config.stateDirectory 'host.stderr.log'
    $process=Start-Process -FilePath $exe -ArgumentList @('--config',('"'+$Configuration+'"'),'--serve') -WindowStyle Hidden -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
    $limit=[Diagnostics.Stopwatch]::StartNew()
    while (-not (Current-Identity) -and -not $process.HasExited -and $limit.Elapsed.TotalSeconds -lt 5) { Start-Sleep -Milliseconds 100; $process.Refresh() }
    if ($process.HasExited -or -not (Current-Identity)) { throw "Startup failed. Inspect $err" }
    if ($Role -eq 'Controller') { Invoke-Role -Arguments @('--status') }
    else { Write-Output 'Agent process started. Confirm authenticated desktop availability using Controller --status --machine.' }
}
switch ($Action) {
    'Initialize' {
        if (-not $StateDirectory) { throw 'StateDirectory is required.' }
        if (Current-Identity) { throw 'Stop the role before changing configuration.' }
        $saved=$null
        if (Test-Path -LiteralPath $Configuration) { $saved=$Configuration+'.'+(Get-Date -Format 'yyyyMMddHHmmssfff')+'.bak'; Copy-Item -LiteralPath $Configuration -Destination $saved; Remove-Item -LiteralPath $Configuration }
        try {
            if ($Role -eq 'Controller') { Invoke-Role -Arguments @('--initialize',$StateDirectory,$HostAddress,[string]$Port) }
            else { if (-not $MachineId -or -not $Certificate) { throw 'MachineId and public Controller Certificate are required.' }; Invoke-Role -Arguments @('--initialize',$StateDirectory,$MachineId,$HostAddress,[string]$Port,$Certificate) }
        } catch { if ($saved) { Copy-Item -LiteralPath $saved -Destination $Configuration -Force }; throw }
    }
    'Register' { if ($Role -ne 'Controller' -or -not $MachineId -or -not $Certificate) { throw 'Controller, MachineId and public Agent Certificate are required.' }; Invoke-Role -Arguments @('--register',$MachineId,$Certificate) }
    'Start' { Start-Role }
    'Status' { if ($Role -eq 'Controller') { Invoke-Role -Arguments @('--status') } else { $identity=Current-Identity; @{running=($null -ne $identity); identity=$identity} | ConvertTo-Json -Depth 4 -Compress } }
    'Stop' { Stop-Role }
    'Restart' { Stop-Role; Start-Role }
    'Backup' {
        Stop-Role
        $config=Get-Content -LiteralPath $Configuration -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $BackupDirectory) { $BackupDirectory=Join-Path $config.stateDirectory ('backup-'+(Get-Date -Format 'yyyyMMddHHmmssfff')) }
        if (Test-Path -LiteralPath $BackupDirectory) { throw 'Backup destination must be new.' }
        $backupFull=[IO.Path]::GetFullPath($BackupDirectory)
        if (-not $backupFull.StartsWith(([IO.Path]::GetFullPath($config.stateDirectory).TrimEnd('\')+'\'),[StringComparison]::OrdinalIgnoreCase)) { throw 'Backup must be inside the protected state directory.' }
        New-Item -ItemType Directory -Path $BackupDirectory | Out-Null
        Copy-Item -LiteralPath $Configuration -Destination (Join-Path $BackupDirectory 'configuration.json')
        foreach ($name in @('identity.pfx','identity.cer','peers.json')) { $source=Join-Path $config.stateDirectory $name; if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $BackupDirectory } }
        Write-Output $BackupDirectory
    }
    'Restore' {
        if (-not $BackupDirectory) { throw 'BackupDirectory is required.' }
        $config=Get-Content -LiteralPath (Join-Path $BackupDirectory 'configuration.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($name in @('identity.pfx','identity.cer')) {
            if (-not (Test-Path -LiteralPath (Join-Path $BackupDirectory $name) -PathType Leaf)) { throw "Incomplete backup: $name is required." }
        }
        Stop-Role
        foreach ($name in @('identity.pfx','identity.cer','peers.json')) { $source=Join-Path $BackupDirectory $name; if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $config.stateDirectory $name) -Force } }
        # An absent peer file is the empty registration snapshot, not permission to keep newer trust.
        $peers=Join-Path $config.stateDirectory 'peers.json'
        if (-not (Test-Path -LiteralPath (Join-Path $BackupDirectory 'peers.json')) -and (Test-Path -LiteralPath $peers)) { Remove-Item -LiteralPath $peers }
        Copy-Item -LiteralPath (Join-Path $BackupDirectory 'configuration.json') -Destination $Configuration -Force
        Write-Output 'Configuration and peer identity restored. Start explicitly after verifying the configuration.'
    }
    'InstallLogonTask' {
        $taskName='DesktopTestDriver-'+$Role+'-'+([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Configuration))).Replace('/','_')
        if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { throw 'Owned logon task already exists.' }
        $user=[Security.Principal.WindowsIdentity]::GetCurrent().Name
        $taskAction=New-ScheduledTaskAction -Execute $exe -Argument ('--config "'+$Configuration+'" --serve') -WorkingDirectory $PackageRoot
        $principal=New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
        Register-ScheduledTask -TaskName $taskName -Action $taskAction -Trigger (New-ScheduledTaskTrigger -AtLogOn -User $user) -Principal $principal -Description ('DesktopTestDriver owned task: '+$Configuration) | Out-Null
    }
    'RemoveLogonTask' {
        $taskName='DesktopTestDriver-'+$Role+'-'+([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Configuration))).Replace('/','_')
        $task=Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        if ($task -and $task.Description -eq ('DesktopTestDriver owned task: '+$Configuration)) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false }
    }
}
