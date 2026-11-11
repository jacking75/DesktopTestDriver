param(
    [Parameter(Mandatory=$true)][string]$Session,
    [Parameter(Mandatory=$true)][string]$Operation,
    # A hashtable (@{windowId='w1'; ...}) or a JSON string.
    $Arguments = @{},
    [int]$TimeoutMilliseconds = 5000,
    [string]$RequestId,
    [string]$Note,
    [string]$State,
    [string]$Controller = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Controller.exe')
)
# Sends one DesktopTestDriver request and returns the parsed response.
# The request is written as UTF-8 without a BOM and sent with --request FILE, so Korean and other non-ASCII text
# survive Windows PowerShell 5.1. Example:
#   $r = .\Invoke-DtdRequest.ps1 -Session $s -Operation set-value -Arguments @{windowId='w1'; elementId='e5'; snapshotToken='b2d40c2611bd'; value='한글'}
#   $r.status; $r.data
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Controller)) { throw "Controller.exe not found: $Controller (pass -Controller)" }
if (-not $RequestId) { $RequestId = 'r-' + [guid]::NewGuid().ToString('N').Substring(0, 12) }
$argumentObject = if ($Arguments -is [string]) { $Arguments | ConvertFrom-Json } else { $Arguments }
$request = [ordered]@{ requestId = $RequestId; controlSessionId = $Session; operation = $Operation; timeoutMilliseconds = $TimeoutMilliseconds; arguments = $argumentObject }
if ($Note) { $request.note = $Note }
$file = Join-Path $env:TEMP ('dtd-' + $RequestId + '.json')
try {
    [IO.File]::WriteAllText($file, ($request | ConvertTo-Json -Depth 20 -Compress), [Text.UTF8Encoding]::new($false))
    $previous = [Console]::OutputEncoding
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    try {
        $common = @(); if ($State) { $common = @('--state', $State) }
        $raw = & $Controller @common --request $file
    } finally { [Console]::OutputEncoding = $previous }
    ($raw -join "`n") | ConvertFrom-Json
} finally { Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue }
