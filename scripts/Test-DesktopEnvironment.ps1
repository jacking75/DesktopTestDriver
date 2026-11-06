param(
    [Parameter(Mandatory=$true)][string]$Controller,
    [Parameter(Mandatory=$true)][string]$Machine,
    [ValidateSet('shared','dedicated')][string]$ExpectedUsage='shared',
    [string]$Config,
    [string]$Output
)
$ErrorActionPreference='Stop'
$prefix=@()
if($Config){$prefix=@('--config',[IO.Path]::GetFullPath($Config))}
$status=& $Controller @prefix --doctor --machine $Machine | ConvertFrom-Json
$checks=[Collections.Generic.List[object]]::new()
if($LASTEXITCODE -ne 0){
    $checks.Add(@{name='agent';state='unsupported';reason=$status.error.code})
}else{
    $data=$status.data
    $checks.Add(@{name='api2';state=$(if(2 -in $data.supportedApiVersions){'supported'}else{'unsupported'});reason='supportedApiVersions'})
    $checks.Add(@{name='machineUsage';state=$(if($data.machineUsage -eq $ExpectedUsage){'supported'}else{'unsupported'});reason='explicit-agent-configuration'})
    $checks.Add(@{name='interactiveDesktop';state=$(if($data.desktop -eq 'Default' -and $data.interactiveSessionId -gt 0 -and $data.sessionKind -in @('console','rdp')){'supported'}else{'unsupported'});reason='active-console-or-rdp-default-non-elevated'})
    $checks.Add(@{name='activityMonitor';state=$(if($data.activityMonitorState -eq 'monitoring'){'supported'}else{'unknown'});reason=$data.activityMonitorState})
    $checks.Add(@{name='screenCapture';state='unknown';reason='target-window-capture-probe-required'})
    $checks.Add(@{name='physicalPc';state='unknown';reason='operator-confirmation-required'})
    $checks.Add(@{name='humanConcurrentWork';state='unknown';reason='human-observation-required'})
}
$admission=@($checks | Where-Object {$_.name -in @('agent','api2','machineUsage','interactiveDesktop','activityMonitor')})
$result=@{machine=$Machine;at=[DateTimeOffset]::UtcNow.ToString('O');ready=($admission.state -notcontains 'unsupported' -and $admission.state -notcontains 'unknown');readyMeaning='observation-preconditions-only; per-action qualification or live input lease is still required';checks=@($checks)}
$json=$result | ConvertTo-Json -Depth 8
if($Output){$json | Set-Content -LiteralPath $Output -Encoding UTF8}
$json
if(-not $result.ready){exit 2}
