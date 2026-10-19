param([switch]$Package)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet build DesktopTestDriver.slnx -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Independent build failed' }
    if ($Package) {
        $target = Join-Path $PSScriptRoot ('artifacts/package-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $target | Out-Null
        foreach ($project in @('Client','Client.Broker','Client.Watchdog','Server')) {
            dotnet publish "src/$project/$project.csproj" -c Release -r win-x64 --self-contained false -o (Join-Path $target $project) --nologo
            if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
        }
        dotnet publish fixtures/MarkerWindow/MarkerWindow.csproj -c Release -r win-x64 --self-contained false -o (Join-Path $target 'MarkerWindow') --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed: MarkerWindow' }
        foreach ($folder in @('transport','scenarios','fixtures','docs','cli','inventory')) {
            $source = Join-Path $PSScriptRoot $folder
            $destination = Join-Path $target $folder
            Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object {
                $_.Extension -in @('.json','.md','.py','.txt') -and $_.FullName -notmatch '[\\/](bin|obj|__pycache__)[\\/]' -and
                ($folder -ne 'fixtures' -or $_.Name -eq 'generic-desktop.json')
            } | ForEach-Object {
                $copy = Join-Path $destination $_.FullName.Substring($source.Length + 1)
                New-Item -ItemType Directory -Path (Split-Path -Parent $copy) -Force | Out-Null
                Copy-Item -LiteralPath $_.FullName -Destination $copy
            }
        }
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README-10.md') -Destination $target
        $files = @(Get-ChildItem -LiteralPath $target -Recurse -File | Sort-Object FullName | ForEach-Object {
            @{ path=$_.FullName.Substring($target.Length + 1).Replace('\','/'); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); size=$_.Length }
        })
        @{version='0.2.0';protocolVersion=2;profile='core';files=$files} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $target 'package-manifest.json') -Encoding UTF8
        Write-Output $target
    }
} finally { Pop-Location }
