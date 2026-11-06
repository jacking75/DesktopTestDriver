param(
    [Parameter(Mandatory=$true)][string]$Controller,
    [Parameter(Mandatory=$true)][string]$Machine,
    [Parameter(Mandatory=$true)][string]$TargetScope,
    [string]$Config
)
$ErrorActionPreference='Stop'
$prefix=@()
if($Config){$prefix=@('--config',[IO.Path]::GetFullPath($Config))}
$session=$null
try {
    $opened=& $Controller @prefix --session-open --machine $Machine --api-version 2 --mode observe --target-scope $TargetScope | ConvertFrom-Json
    if($LASTEXITCODE -ne 0){throw $opened.error.code}
    $session=$opened.controlSessionId
    $request=@{apiVersion=2;requestId=[guid]::NewGuid().ToString('N');controlSessionId=$session;operation='windows';timeoutMilliseconds=5000;arguments=@{}}
    $windows=($request | ConvertTo-Json -Compress -Depth 8) | & $Controller @prefix --request-stdin | ConvertFrom-Json
    if($LASTEXITCODE -ne 0){throw $windows.error.code}
    foreach($window in $windows.data.windows){
        $request.requestId=[guid]::NewGuid().ToString('N');$request.operation='inspect';$request.arguments=@{windowId=$window.windowId}
        ($request | ConvertTo-Json -Compress -Depth 8) | & $Controller @prefix --request-stdin
        if($LASTEXITCODE -ne 0){throw 'inspect 실패이다.'}
    }
} finally {
    if($session){& $Controller @prefix --session-close $session | Out-Null;if($LASTEXITCODE -ne 0){throw '세션 정리 상태 확인이 필요하다.'}}
}
