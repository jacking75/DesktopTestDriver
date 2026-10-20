param([Parameter(Mandatory=$true)][string]$Package)
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
        foreach($match in [regex]::Matches($text,'\[[^\]]*\]\(([^)]+)\)')){
            $link=$match.Groups[1].Value.Trim('<','>')
            if($link -match '^[a-zA-Z]+://' -or $link.StartsWith('#')){continue}
            $link=[Uri]::UnescapeDataString(($link -split '#')[0])
            $resolved=[IO.Path]::GetFullPath((Join-Path $document.DirectoryName $link))
            if(-not $resolved.StartsWith($destination+'\',[StringComparison]::OrdinalIgnoreCase) -or -not(Test-Path -LiteralPath $resolved)){throw "Broken packaged link: $relative -> $link"}
        }
    }
    $help=& (Join-Path $destination ($role+'.exe')) --help
    if($LASTEXITCODE -ne 0 -or ($help -join "`n") -notmatch 'GUI API 1' -or -not(($help -join "`n").Contains((Join-Path $destination 'docs\agent-usage.md')))){throw "Packaged help mismatch: $role"}
}
$controller=Join-Path $isolated 'Controller'; $agent=Join-Path $isolated 'Agent'
$controllerState=Join-Path $isolated 'controller-state';$agentState=Join-Path $isolated 'agent-state'
$probe=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$probe.Start();$port=$probe.LocalEndpoint.Port;$probe.Stop()
$controllerManage=Join-Path $controller 'scripts/manage.ps1';$agentManage=Join-Path $agent 'scripts/manage.ps1'
try {
    & $controllerManage -Action Initialize -StateDirectory $controllerState -Port $port | Out-Null
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
    & $controllerManage -Action Backup | Out-Null
    $backup=(Get-ChildItem -LiteralPath $controllerState -Directory -Filter 'backup-*' | Select-Object -First 1).FullName
    & $controllerManage -Action Restore -BackupDirectory $backup | Out-Null
    @{isolated=$isolated;roles=2;links='valid';manifestHashes='valid';lifecycle='valid';desktopInput=$false} | ConvertTo-Json -Compress
} finally {
    & $agentManage -Action Stop | Out-Null
    & $controllerManage -Action Stop | Out-Null
}
