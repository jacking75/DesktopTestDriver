param([switch]$UiTests)
# Release build, package, contract/handler/TCP tests, packaged lifecycle check. -UiTests also runs the real desktop
# tests (fixture windows, Settings, Control Panel; UIA only, no keyboard or mouse input).
$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot
try {
    $evidence=Join-Path $PSScriptRoot ('artifacts/verify-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $evidence | Out-Null
    $output=@(& ./build.ps1 -Package | Tee-Object -FilePath (Join-Path $evidence 'build.txt'))
    $package=[string]$output[-1]
    if(-not(Test-Path -LiteralPath (Join-Path $package 'Controller/package-manifest.json'))){throw 'Package missing'}
    & ./bin/Release/Driver.Tests/Driver.Tests.exe | Tee-Object -FilePath (Join-Path $evidence 'local-tests.txt')
    if($LASTEXITCODE -ne 0){throw 'Contract/handler/TCP tests failed'}
    $ui='NOT RUN'
    if($UiTests){
        & ./bin/Release/Driver.Tests/Driver.Tests.exe --ui-tests | Tee-Object -FilePath (Join-Path $evidence 'ui-tests.txt')
        if($LASTEXITCODE -ne 0){throw 'Real desktop tests failed'}
        $ui='passed'
    }
    & ./scripts/check-package.ps1 -Package $package | Tee-Object -FilePath (Join-Path $evidence 'package-tests.txt')
    @{at=[DateTimeOffset]::UtcNow.ToString('O');package=$package;evidence=$evidence;wireVersion=3;uiTests=$ui;realDesktopInput='NOT RUN';twoPhysicalMachines='NOT RUN'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'summary.json') -Encoding UTF8
    Write-Output "Evidence: $evidence"
} finally {Pop-Location}
