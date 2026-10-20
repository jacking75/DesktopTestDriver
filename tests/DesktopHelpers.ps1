# Explicit, bounded real-desktop verification helpers. Not called by verify.ps1 or shipped in role packages.
param([string]$EnvironmentFile)
$ErrorActionPreference='Stop'
function Start-DesktopRound([string]$Package,[string]$Round,[int]$Seconds=300) {
    $root=Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts/desktop-'+$Round+'-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root | Out-Null
    $controller=Join-Path $Package 'Controller/Controller.exe';$agent=Join-Path $Package 'Agent/Agent.exe'
    $controllerConfig=Join-Path $root 'Controller.json';$agentConfig=Join-Path $root 'Agent.json'
    $controllerState=Join-Path $root 'controller-state';$agentState=Join-Path $root 'agent-state'
    $probe=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$probe.Start();$port=$probe.LocalEndpoint.Port;$probe.Stop()
    & $controller --config $controllerConfig --initialize $controllerState 127.0.0.1 $port | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Controller initialization failed'}
    & $agent --config $agentConfig --initialize $agentState desktop-test 127.0.0.1 $port (Join-Path $controllerState 'identity.cer') | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Agent initialization failed'}
    & $controller --config $controllerConfig --register desktop-test (Join-Path $agentState 'identity.cer') | Out-Null
    $controllerProcess=Start-Process -FilePath $controller -ArgumentList @('--config',('"'+$controllerConfig+'"'),'--serve') -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $root 'controller.stderr.log')
    $agentProcess=Start-Process -FilePath $agent -ArgumentList @('--config',('"'+$agentConfig+'"'),'--serve') -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $root 'agent.stderr.log')
    $ready=$false;$timer=[Diagnostics.Stopwatch]::StartNew()
    while($timer.Elapsed.TotalSeconds -lt 10){$status=& $controller --config $controllerConfig --status --machine desktop-test | ConvertFrom-Json;if($LASTEXITCODE -eq 0){$ready=$true;break};Start-Sleep -Milliseconds 100}
    if(-not $ready){& $agent --config $agentConfig --stop;& $controller --config $controllerConfig --shutdown;throw 'Desktop not ready'}
    $session=& $controller --config $controllerConfig --session-open --machine desktop-test --mode control | ConvertFrom-Json
    if($LASTEXITCODE -ne 0){throw 'Control session failed'}
    $environment=@{root=$root;package=$Package;round=$Round;controller=$controller;agent=$agent;controllerConfig=$controllerConfig;agentConfig=$agentConfig;agentState=$agentState;sessionId=$session.controlSessionId;startedTick=[Diagnostics.Stopwatch]::GetTimestamp();maximumSeconds=$Seconds}
    $file=Join-Path $root 'environment.json';$environment | ConvertTo-Json | Set-Content -LiteralPath $file -Encoding UTF8
    $status | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $root 'initial-status.json') -Encoding UTF8
    return $file
}
if($EnvironmentFile){$script:DesktopEnvironment=Get-Content -LiteralPath $EnvironmentFile -Raw -Encoding UTF8 | ConvertFrom-Json}
function Stop-DesktopRound([string]$Outcome='stopped',[string]$Reason='') {
    $e=$script:DesktopEnvironment
    & $e.controller --config $e.controllerConfig --session-close $e.sessionId | Set-Content -LiteralPath (Join-Path $e.root 'cleanup-session.json') -Encoding UTF8
    & $e.agent --config $e.agentConfig --stop
    Start-Sleep -Milliseconds 300
    & $e.controller --config $e.controllerConfig --shutdown | Out-Null
    @{outcome=$Outcome;reason=$Reason;finishedAt=[DateTimeOffset]::UtcNow.ToString('O')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $e.root 'round-result.json') -Encoding UTF8
}
function Invoke-Desktop([string]$Operation,[hashtable]$Arguments,[int]$TimeoutMilliseconds=5000,[string]$Expected='completed') {
    $e=$script:DesktopEnvironment
    if(([Diagnostics.Stopwatch]::GetTimestamp()-$e.startedTick)/[double][Diagnostics.Stopwatch]::Frequency -gt $e.maximumSeconds){Stop-DesktopRound 'failed' 'RoundDeadline';throw 'Round deadline reached'}
    $id='r-'+[guid]::NewGuid().ToString('N');$file=Join-Path $e.root ($id+'.request.json')
    @{apiVersion=1;requestId=$id;controlSessionId=$e.sessionId;operation=$Operation;timeoutMilliseconds=$TimeoutMilliseconds;arguments=$Arguments} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $file -Encoding UTF8
    $raw=& $e.controller --config $e.controllerConfig --request $file
    $raw | Set-Content -LiteralPath (Join-Path $e.root ($id+'.response.json')) -Encoding UTF8
    $response=$raw | ConvertFrom-Json
    if($response.status -ne $Expected){Stop-DesktopRound 'failed' ($Operation+':'+$response.status+':'+$response.error.code);throw ($raw -join "`n")}
    return $response
}
