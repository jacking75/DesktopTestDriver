param([switch]$Package)
$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot
try {
 dotnet restore DesktopTestDriver.slnx --locked-mode --nologo
 if($LASTEXITCODE -ne 0){throw 'Locked restore failed'}
 dotnet build DesktopTestDriver.slnx -c Release --no-restore --nologo
 if($LASTEXITCODE -ne 0){throw 'Build failed'}
 if($Package){
  $target=Join-Path $PSScriptRoot ('artifacts/package-'+[guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Path $target | Out-Null
  $documents=@('README.md','docs/agent-usage.md','docs/agent-driven-desktop-control.md','docs_working/간단한-GUI-테스트-사용매뉴얼-11.md','docs_working/RemoteAssist-E2E-테스트-사용매뉴얼-12.md','docs_working/GUI-사용매뉴얼-기반-상세구현계획-13.md')
  foreach($role in @('Controller','Agent')){
   $destination=Join-Path $target $role
   dotnet publish "src/$role/$role.csproj" -c Release -r win-x64 --no-restore --self-contained false -o $destination --nologo
   if($LASTEXITCODE -ne 0){throw "Publish failed: $role"}
   foreach($document in $documents){$to=Join-Path $destination $document;New-Item -ItemType Directory -Path (Split-Path -Parent $to) -Force | Out-Null;Copy-Item -LiteralPath (Join-Path $PSScriptRoot $document) -Destination $to}
   New-Item -ItemType Directory -Path (Join-Path $destination 'scripts') | Out-Null
   Copy-Item -LiteralPath scripts/manage.ps1 -Destination (Join-Path $destination 'scripts/manage.ps1')
   Copy-Item -LiteralPath LICENSE -Destination $destination
   $files=@(Get-ChildItem -LiteralPath $destination -Recurse -File | Sort-Object FullName | ForEach-Object {@{path=$_.FullName.Substring($destination.Length+1).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
   @{version='0.3.0';apiVersion=1;role=$role;sourceRevision=(git rev-parse HEAD);workingTreeDirty=([bool](git status --porcelain));files=$files} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $destination 'package-manifest.json') -Encoding UTF8
  }
  foreach($fixture in @('SimpleMemo','InputWindow')){
   dotnet publish "fixtures/$fixture/$fixture.csproj" -c Release -r win-x64 --no-restore --self-contained false -o (Join-Path $target "test-apps/$fixture") --nologo
   if($LASTEXITCODE -ne 0){throw "Fixture publish failed: $fixture"}
  }
  Write-Output $target
 }
}finally{Pop-Location}
