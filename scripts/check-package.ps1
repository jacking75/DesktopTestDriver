param([Parameter(Mandatory=$true)][string]$Package,[switch]$OfflineOnly)
$ErrorActionPreference='Stop'
$repository=Split-Path -Parent $PSScriptRoot
$isolated=Join-Path $repository ('artifacts/isolated-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $isolated | Out-Null
foreach($role in @('Controller','Agent')) {
    $source=Join-Path $Package $role
    $destination=Join-Path $isolated $role
    Copy-Item -LiteralPath $source -Destination $destination -Recurse
    $manifest=Get-Content -LiteralPath (Join-Path $destination 'package-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if($manifest.apiVersion -ne 1 -or $manifest.role -ne $role){throw 'Manifest API/role mismatch'}
    foreach($entry in $manifest.files){
        $path=Join-Path $destination $entry.path
        if(-not(Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256){throw "Package hash mismatch: $path"}
    }
    foreach($document in Get-ChildItem -LiteralPath $destination -Recurse -Filter *.md){
        $text=Get-Content -LiteralPath $document.FullName -Raw -Encoding UTF8
        $relative=$document.FullName.Substring($destination.Length+1)
        if(Test-Path -LiteralPath (Join-Path $repository $relative)){
            if((Get-FileHash -LiteralPath $document.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $repository $relative)).Hash){throw "Source document mismatch: $relative"}
        }
        $links=@([regex]::Matches($text,'\[[^\]]*\]\(([^)]+)\)') | ForEach-Object {$_.Groups[1].Value})
        $links+=@([regex]::Matches($text,'\b(?:src|srcset|href)="([^"]+)"') | ForEach-Object {$_.Groups[1].Value})
        foreach($reference in $links){
            $link=$reference.Trim('<','>')
            if($link -match '^[a-zA-Z]+://' -or $link.StartsWith('#')){continue}
            $link=[Uri]::UnescapeDataString(($link -split '#')[0])
            $resolved=[IO.Path]::GetFullPath((Join-Path $document.DirectoryName $link))
            if(-not $resolved.StartsWith($destination+'\',[StringComparison]::OrdinalIgnoreCase) -or -not(Test-Path -LiteralPath $resolved)){throw "Broken packaged link: $relative -> $link"}
        }
    }
    $help=& (Join-Path $destination ($role+'.exe')) --help
    if($LASTEXITCODE -ne 0 -or ($help -join "`n") -notmatch 'GUI API 1' -or -not(($help -join "`n").Contains((Join-Path $destination 'docs\agent-usage.md')))){throw "Packaged help mismatch: $role"}
}
Copy-Item -LiteralPath (Join-Path $Package 'test-apps') -Destination (Join-Path $isolated 'test-apps') -Recurse
$example=Join-Path $isolated 'Controller/examples/standalone/Inspect.csproj'
& dotnet build $example -c Release -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false --nologo | Tee-Object -FilePath (Join-Path $isolated 'sdk-example-build.txt') | Out-Null
if($LASTEXITCODE -ne 0){throw 'Isolated SDK example did not compile from distributed DLLs'}
if($OfflineOnly){
    @{isolated=$isolated;roles=2;links='valid';manifestHashes='valid';sdkExample='compiled-from-distributed-DLLs';desktopLifecycle='NOT_RUN';reason='no-active-user-desktop'} | ConvertTo-Json -Compress
    return
}
$controller=Join-Path $isolated 'Controller'; $agent=Join-Path $isolated 'Agent'
$controllerState=Join-Path $isolated 'controller-state';$agentState=Join-Path $isolated 'agent-state'
$probe=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$probe.Start();$port=$probe.LocalEndpoint.Port;$probe.Stop()
$controllerManage=Join-Path $controller 'scripts/manage.ps1';$agentManage=Join-Path $agent 'scripts/manage.ps1'
try {
    foreach($role in @('Controller','Agent')) {
        $invalidConfig=Join-Path $isolated ($role+'-invalid.json');$invalidState=Join-Path $isolated ($role+'-invalid-state')
        $invalidArguments=if($role -eq 'Controller'){@('--initialize',$invalidState,'127.0.0.1','0')}else{@('--initialize',$invalidState,'invalid machine','127.0.0.1',[string]$port,(Join-Path $controllerState 'identity.cer'))}
        if($role -eq 'Agent') { & $controllerManage -Action Initialize -StateDirectory $controllerState -Port $port | Out-Null }
        $previousError=$ErrorActionPreference
        try { $ErrorActionPreference='Continue'; $invalid=@(& (Join-Path (Join-Path $isolated $role) ($role+'.exe')) --config $invalidConfig @invalidArguments 2>&1);$invalidExit=$LASTEXITCODE }
        finally { $ErrorActionPreference=$previousError }
        if($invalidExit -eq 0 -or ($invalid -join "`n") -notmatch 'InvalidConfiguration' -or (Test-Path -LiteralPath $invalidConfig) -or (Test-Path -LiteralPath (Join-Path $invalidState 'identity.pfx'))){throw "Invalid initialization did not reject configuration before creating identity: $role"}
    }
    $emptyPeersBackup=@(& $controllerManage -Action Backup)[-1]
    & $agentManage -Action Initialize -StateDirectory $agentState -MachineId package-smoke -Port $port -Certificate (Join-Path $controllerState 'identity.cer') | Out-Null
    & $controllerManage -Action Register -MachineId package-smoke -Certificate (Join-Path $agentState 'identity.cer') | Out-Null
    & $controllerManage -Action Start | Out-Null
    & $agentManage -Action Start | Out-Null
    $timer=[Diagnostics.Stopwatch]::StartNew();$connected=$false
    while($timer.Elapsed.TotalSeconds -lt 8){
        $status=& (Join-Path $controller 'Controller.exe') --status --machine package-smoke | ConvertFrom-Json
        if($LASTEXITCODE -eq 0 -and $status.data.agentRunning){$connected=$true;break};Start-Sleep -Milliseconds 100
    }
    if(-not $connected){throw 'Isolated Agent did not authenticate and publish desktop readiness'}
    & $agentManage -Action Restart | Out-Null
    & $agentManage -Action Stop | Out-Null
    & $controllerManage -Action Restart | Out-Null
    # 배포 폴더 밖의 제품 저장소를 참조하지 않고 자체 fixture를 관측한다.
    & $controllerManage -Action Start | Out-Null
    & $agentManage -Action Start | Out-Null
    $fixtureExe=Join-Path $isolated 'test-apps/FormsWorkbench/FormsWorkbench.exe'
    $fixture=Start-Process -FilePath $fixtureExe -ArgumentList '--quiet' -WindowStyle Hidden -PassThru
    try {
        $null=$fixture.WaitForInputIdle(3000)
        $scopeFile=Join-Path $isolated 'example-scope.json'
        $scope=@(@{pid=$fixture.Id;createdAt=$fixture.StartTime.ToUniversalTime().ToString('O');imagePath=$fixtureExe;imageHash=(Get-FileHash -LiteralPath $fixtureExe -Algorithm SHA256).Hash.ToLowerInvariant()})
        [IO.File]::WriteAllText($scopeFile,(ConvertTo-Json -InputObject $scope),[Text.UTF8Encoding]::new($false))
        $exampleDll=Get-ChildItem -LiteralPath (Join-Path (Split-Path -Parent $example) 'bin/Release') -Filter Inspect.dll -Recurse | Select-Object -First 1 -ExpandProperty FullName
        & dotnet $exampleDll (Join-Path $controller 'Controller.json') package-smoke $scopeFile | Set-Content -LiteralPath (Join-Path $isolated 'sdk-example-result.jsonl') -Encoding UTF8
        if($LASTEXITCODE -ne 0){throw 'Isolated SDK fixture observation failed'}
    } finally {
        if(-not $fixture.HasExited){Stop-Process -Id $fixture.Id -Force;$null=$fixture.WaitForExit(3000)}
    }
    $backup=@(& $controllerManage -Action Backup)[-1]
    & $controllerManage -Action Restore -BackupDirectory $backup | Out-Null
    if(-not(Test-Path -LiteralPath (Join-Path $controllerState 'peers.json'))){throw 'Restore lost the saved registrations'}
    $incomplete=Join-Path $controllerState 'backup-incomplete';New-Item -ItemType Directory -Path $incomplete | Out-Null
    Copy-Item -LiteralPath (Join-Path $backup 'configuration.json') -Destination $incomplete
    $identityHash=(Get-FileHash -LiteralPath (Join-Path $controllerState 'identity.pfx')).Hash
    try { & $controllerManage -Action Restore -BackupDirectory $incomplete | Out-Null;throw 'Incomplete backup was accepted' }
    catch { if($_.Exception.Message -notmatch '^Incomplete backup:'){throw} }
    if((Get-FileHash -LiteralPath (Join-Path $controllerState 'identity.pfx')).Hash -ne $identityHash){throw 'Incomplete restore changed identity'}
    & $controllerManage -Action Restore -BackupDirectory $emptyPeersBackup | Out-Null
    if(Test-Path -LiteralPath (Join-Path $controllerState 'peers.json')){throw 'Restore retained registrations absent from the backup'}
    @{isolated=$isolated;roles=2;links='valid';manifestHashes='valid';lifecycle='valid';invalidInitialization='rejected';incompleteBackup='rejected';emptyRegistrationRestore='valid';desktopInput=$false;sdkExample='compiled-and-observed-own-fixture'} | ConvertTo-Json -Compress
} finally {
    & $agentManage -Action Stop | Out-Null
    & $controllerManage -Action Stop | Out-Null
}
