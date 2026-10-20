param()
$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot
try {
    $evidence=Join-Path $PSScriptRoot ('artifacts/verify-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $evidence | Out-Null
    $output=@(& ./build.ps1 -Package | Tee-Object -FilePath (Join-Path $evidence 'build.txt'))
    $package=[string]$output[-1]
    if(-not(Test-Path -LiteralPath (Join-Path $package 'Controller/package-manifest.json'))){throw 'Package missing'}
    $previousController=$env:DTD_TEST_CONTROLLER
    try {
        $env:DTD_TEST_CONTROLLER=Join-Path $package 'Controller/Controller.exe'
        & ./bin/Release/Driver.Tests/Driver.Tests.exe | Tee-Object -FilePath (Join-Path $evidence 'local-tests.txt')
        if($LASTEXITCODE -ne 0){throw 'Local contract/transport/session/process validation failed'}
    } finally {$env:DTD_TEST_CONTROLLER=$previousController}
    & ./scripts/check-package.ps1 -Package $package | Tee-Object -FilePath (Join-Path $evidence 'package-tests.txt')
    @{at=[DateTimeOffset]::UtcNow.ToString('O');package=$package;evidence=$evidence;apiVersion=1;realDesktopInput=$false;twoPhysicalMachines='NOT RUN';remoteAssist='NOT RUN'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'summary.json') -Encoding UTF8
    Write-Output "Evidence: $evidence"
} finally {Pop-Location}
