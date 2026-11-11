param([Parameter(Mandatory=$true)][string]$Package,[switch]$OfflineOnly)
# Checks a published package in an isolated copy: manifest hashes, document links, help text, the SDK example built
# from the shipped DLLs, and (unless -OfflineOnly) a real start -> session -> launch -> inspect -> close -> stop cycle.
$ErrorActionPreference='Stop'
$repository=Split-Path -Parent $PSScriptRoot
$isolated=Join-Path $repository ('artifacts/isolated-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $isolated | Out-Null
foreach($role in @('Controller','Agent')) {
    $destination=Join-Path $isolated $role
    Copy-Item -LiteralPath (Join-Path $Package $role) -Destination $destination -Recurse
    $manifest=Get-Content -LiteralPath (Join-Path $destination 'package-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if($manifest.role -ne $role -or $manifest.wireVersion -ne 3){throw 'Manifest role/wire mismatch'}
    foreach($entry in $manifest.files){
        $path=Join-Path $destination $entry.path
        if(-not(Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256){throw "Package hash mismatch: $path"}
    }
    foreach($document in Get-ChildItem -LiteralPath $destination -Recurse -Filter *.md){
        $text=Get-Content -LiteralPath $document.FullName -Raw -Encoding UTF8
        $relative=$document.FullName.Substring($destination.Length+1)
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
    $help=(& (Join-Path $destination ($role+'.exe')) --help) -join "`n"
    if($LASTEXITCODE -ne 0 -or $help -notmatch 'wire 3' -or -not $help.Contains((Join-Path $destination 'docs\agent-usage.md'))){throw "Packaged help mismatch: $role"}
}
Copy-Item -LiteralPath (Join-Path $Package 'test-apps') -Destination (Join-Path $isolated 'test-apps') -Recurse
$example=Join-Path $isolated 'Controller/examples/standalone/Inspect.csproj'
& dotnet build $example -c Release -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false --nologo | Tee-Object -FilePath (Join-Path $isolated 'sdk-example-build.txt') | Out-Null
if($LASTEXITCODE -ne 0){throw 'Isolated SDK example did not compile from distributed DLLs'}
if($OfflineOnly){
    @{isolated=$isolated;roles=2;links='valid';manifestHashes='valid';sdkExample='compiled-from-distributed-DLLs';desktopLifecycle='NOT_RUN'} | ConvertTo-Json -Compress
    return
}
$controller=Join-Path $isolated 'Controller'; $agent=Join-Path $isolated 'Agent'
$controllerState=Join-Path $isolated 'controller-state'; $agentState=Join-Path $isolated 'agent-state'
$port=25001+(Get-Random -Maximum 199); $endpoint="127.0.0.1:$port"
$controllerManage=Join-Path $controller 'scripts/manage.ps1'; $agentManage=Join-Path $agent 'scripts/manage.ps1'
$controllerExe=Join-Path $controller 'Controller.exe'
try {
    & $controllerManage -Action Start -StateDirectory $controllerState -Endpoint $endpoint | Out-Null
    & $agentManage -Action Start -StateDirectory $agentState -Endpoint $endpoint -MachineId package-smoke | Out-Null
    $timer=[Diagnostics.Stopwatch]::StartNew(); $connected=$false
    while($timer.Elapsed.TotalSeconds -lt 10){
        $status=& $controllerExe --state $controllerState --status --machine package-smoke | ConvertFrom-Json
        if($LASTEXITCODE -eq 0 -and $status.data.agentRunning){$connected=$true;break}; Start-Sleep -Milliseconds 100
    }
    if(-not $connected){throw 'Agent did not connect to the controller'}
    $fixture=Join-Path $isolated 'test-apps/FormsWorkbench/FormsWorkbench.exe'
    $opened=& $controllerExe --state $controllerState --session-open --machine package-smoke --launch $fixture --arg --quiet | ConvertFrom-Json
    if($LASTEXITCODE -ne 0){throw ('session-open failed: '+$opened.error.code)}
    $session=$opened.controlSessionId; $window=$opened.data.launch.window.windowId
    $inspect=& $controllerExe --state $controllerState --inspect --session $session --window $window | ConvertFrom-Json
    if($LASTEXITCODE -ne 0 -or $inspect.data.tree -notmatch '#Apply'){throw 'inspect did not show the fixture'}
    & dotnet (Get-ChildItem -LiteralPath (Join-Path (Split-Path -Parent $example) 'bin/Release') -Filter Inspect.dll -Recurse | Select-Object -First 1 -ExpandProperty FullName) $controllerState package-smoke $fixture | Set-Content -LiteralPath (Join-Path $isolated 'sdk-example-result.txt') -Encoding UTF8
    if($LASTEXITCODE -ne 0){throw 'SDK example failed against the packaged hosts'}
    $closed=& $controllerExe --state $controllerState --session-close $session | ConvertFrom-Json
    if($closed.data.state -ne 'closed' -or $closed.data.terminatedProcessIds.Count -ne 1){throw 'session close did not stop the launched fixture'}
    & $agentManage -Action Restart -StateDirectory $agentState -Endpoint $endpoint -MachineId package-smoke | Out-Null
    @{isolated=$isolated;roles=2;links='valid';manifestHashes='valid';lifecycle='valid';sdkExample='compiled-and-ran-against-package';desktopInput=$false} | ConvertTo-Json -Compress
} finally {
    & $agentManage -Action Stop -StateDirectory $agentState | Out-Null
    & $controllerManage -Action Stop -StateDirectory $controllerState | Out-Null
}
