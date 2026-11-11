param(
    [Parameter(Mandatory=$true)][ValidateSet('Start','Status','Stop','Restart','InstallLogonTask','RemoveLogonTask')][string]$Action,
    [ValidateSet('Controller','Agent')][string]$Role,
    [string]$PackageRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$StateDirectory,
    # Controller: address to listen on. Agent: controller address to connect to.
    [string]$Endpoint = '127.0.0.1:25001',
    [string]$MachineId,
    # InstallLogonTask: run with highest privileges so elevated tools (regedit, services.msc) can be driven.
    [switch]$Highest
)
# Start/stop helper. No configuration file, certificate or registration is needed: everything is a command-line option.
# Trusted networks and PCs only. The connection is not encrypted or authenticated.
$ErrorActionPreference = 'Stop'
if (-not $Role) {
    if (Test-Path -LiteralPath (Join-Path $PackageRoot 'Controller.exe')) { $Role='Controller' }
    elseif (Test-Path -LiteralPath (Join-Path $PackageRoot 'Agent.exe')) { $Role='Agent' }
    else { throw 'Role or a published package root is required.' }
}
$exe = Join-Path $PackageRoot ($Role+'.exe')
if (-not (Test-Path -LiteralPath $exe)) { throw "Executable not found: $exe" }
if (-not $StateDirectory) { $StateDirectory = Join-Path $env:LOCALAPPDATA ('DesktopTestDriver\'+$Role) }
$StateDirectory = [IO.Path]::GetFullPath($StateDirectory)
function Arguments {
    $a = @('--state', $StateDirectory)
    if ($Role -eq 'Controller') { $a += @('--listen', $Endpoint) } else { $a += @('--controller', $Endpoint); if ($MachineId) { $a += @('--machine', $MachineId) } }
    return $a
}
function Running {
    $file = Join-Path $StateDirectory ($Role.ToLowerInvariant()+'-process.json')
    if (-not (Test-Path -LiteralPath $file)) { return $null }
    $identity = Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json
    $process = Get-Process -Id $identity.pid -ErrorAction SilentlyContinue
    if (-not $process) { return $null }
    try { if ($process.Path -ne $identity.imagePath -or $process.HasExited) { return $null } } catch { return $null }
    return $identity
}
function Stop-Role {
    if (-not (Running)) { return }
    if ($Role -eq 'Controller') { & $exe --state $StateDirectory --shutdown | Out-Null } else { & $exe --state $StateDirectory --stop | Out-Null }
    $limit = [Diagnostics.Stopwatch]::StartNew()
    while ((Running) -and $limit.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 100 }
    if (Running) { throw 'Graceful stop did not finish within 10 seconds.' }
}
function Start-Role {
    if (Running) { throw "$Role is already running." }
    New-Item -ItemType Directory -Path $StateDirectory -Force | Out-Null
    $err = Join-Path $StateDirectory 'host.stderr.log'; $out = Join-Path $StateDirectory 'host.stdout.log'
    $quoted = @(Arguments | ForEach-Object { '"'+$_+'"' }) + @('--serve')
    $process = Start-Process -FilePath $exe -ArgumentList $quoted -WindowStyle Hidden -PassThru -RedirectStandardError $err -RedirectStandardOutput $out
    $limit = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Running) -and -not $process.HasExited -and $limit.Elapsed.TotalSeconds -lt 5) { Start-Sleep -Milliseconds 100; $process.Refresh() }
    if ($process.HasExited -or -not (Running)) { throw "Startup failed. See $err" }
    @{role=$Role; pid=$process.Id; state=$StateDirectory; endpoint=$Endpoint} | ConvertTo-Json -Compress
}
$taskName = 'DesktopTestDriver-'+$Role+'-'+(([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($StateDirectory.ToUpperInvariant())) | ForEach-Object { $_.ToString('x2') }) -join '').Substring(0,12)
switch ($Action) {
    'Start' { Start-Role }
    'Stop' { Stop-Role }
    'Restart' { Stop-Role; Start-Role }
    'Status' {
        if ($Role -eq 'Controller' -and (Running)) { & $exe --state $StateDirectory --status }
        else { $identity = Running; @{role=$Role; running=($null -ne $identity); identity=$identity; state=$StateDirectory} | ConvertTo-Json -Depth 4 -Compress }
    }
    'InstallLogonTask' {
        # The agent must run inside the logged-on desktop session, so the task starts at logon as that user.
        if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { throw "Logon task already exists: $taskName" }
        $user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        $argumentLine = ((Arguments | ForEach-Object { '"'+$_+'"' }) + @('--serve')) -join ' '
        $taskAction = New-ScheduledTaskAction -Execute $exe -Argument $argumentLine -WorkingDirectory $PackageRoot
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel $(if ($Highest) { 'Highest' } else { 'Limited' })
        Register-ScheduledTask -TaskName $taskName -Action $taskAction -Trigger (New-ScheduledTaskTrigger -AtLogOn -User $user) -Principal $principal -Description ('DesktopTestDriver '+$Role+': '+$StateDirectory) | Out-Null
        Write-Output $taskName
    }
    'RemoveLogonTask' { if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false } }
}
