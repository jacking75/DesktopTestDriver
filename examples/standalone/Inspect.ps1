param(
    [Parameter(Mandatory=$true)][string]$Controller,
    [Parameter(Mandatory=$true)][string]$Executable,
    [string]$Machine,
    [string]$StateDirectory
)
# Opens a session that launches an application, prints the compact tree of its first window, then closes the session
# (which also stops the application).
$ErrorActionPreference='Stop'
$common=@(); if($StateDirectory){$common=@('--state',[IO.Path]::GetFullPath($StateDirectory))}
$machineArgs=@(); if($Machine){$machineArgs=@('--machine',$Machine)}
$session=$null
try {
    $opened=& $Controller @common --session-open @machineArgs --launch $Executable | ConvertFrom-Json
    if($LASTEXITCODE -ne 0){throw $opened.error.code}
    $session=$opened.controlSessionId
    $window=$opened.data.launch.window.windowId
    $request=@{requestId=[guid]::NewGuid().ToString('N');controlSessionId=$session;operation='inspect';arguments=@{windowId=$window;maxDepth=6}}
    $inspected=($request | ConvertTo-Json -Compress -Depth 8) | & $Controller @common --request-stdin | ConvertFrom-Json
    if($LASTEXITCODE -ne 0){throw $inspected.error.code}
    $inspected.data.tree
} finally {
    if($session){& $Controller @common --session-close $session | Out-Null}
}
