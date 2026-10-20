param([switch]$Package,[ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot
try {
 dotnet restore DesktopTestDriver.slnx --locked-mode --nologo
 if($LASTEXITCODE -ne 0){throw 'Locked restore failed'}
 dotnet build DesktopTestDriver.slnx -c $Configuration --no-restore --nologo
 if($LASTEXITCODE -ne 0){throw 'Build failed'}
 if($Package){
  $target=Join-Path $PSScriptRoot ('bin/live_'+$Configuration)
  New-Item -ItemType Directory -Path $target -Force | Out-Null
  $documents=@('README.md','README_kr.md','docs/agent-usage.md','docs/gui-test-guide.md')
  $assets=@(
   'docs/assets/remote-gui-hero.png','docs/assets/architecture.svg','docs/assets/control-loop.gif','docs/assets/control-loop.png',
   'docs/assets/remote-gui-hero-en.png','docs/assets/architecture-en.svg','docs/assets/control-loop-en.gif','docs/assets/control-loop-en.png'
  )
  foreach($role in @('Controller','Agent')){
   $destination=Join-Path $target $role
   dotnet publish "src/$role/$role.csproj" -c $Configuration -r win-x64 --no-restore --self-contained false -o $destination --nologo
   if($LASTEXITCODE -ne 0){throw "Publish failed: $role"}
   # Reused publish folders must not retain documentation removed from the package.
   foreach($oldDocument in Get-ChildItem -LiteralPath $destination -Recurse -File -Filter *.md){
    $relative=$oldDocument.FullName.Substring($destination.Length+1).Replace('\','/')
    if($relative -notin $documents){Remove-Item -LiteralPath $oldDocument.FullName}
   }
   $assetDirectory=Join-Path $destination 'docs/assets'
   if(Test-Path -LiteralPath $assetDirectory){
    foreach($oldAsset in Get-ChildItem -LiteralPath $assetDirectory -File){
     $relative=$oldAsset.FullName.Substring($destination.Length+1).Replace('\','/')
     if($relative -notin $assets){Remove-Item -LiteralPath $oldAsset.FullName}
    }
   }
   foreach($document in ($documents+$assets)){$to=Join-Path $destination $document;New-Item -ItemType Directory -Path (Split-Path -Parent $to) -Force | Out-Null;Copy-Item -LiteralPath (Join-Path $PSScriptRoot $document) -Destination $to}
   New-Item -ItemType Directory -Path (Join-Path $destination 'scripts') -Force | Out-Null
   Copy-Item -LiteralPath scripts/manage.ps1 -Destination (Join-Path $destination 'scripts/manage.ps1')
   Copy-Item -LiteralPath LICENSE -Destination $destination
   $manifest=Join-Path $destination 'package-manifest.json'
   $files=@(Get-ChildItem -LiteralPath $destination -Recurse -File | Where-Object {$_.FullName -ne $manifest} | Sort-Object FullName | ForEach-Object {@{path=$_.FullName.Substring($destination.Length+1).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
   @{version='0.3.0';apiVersion=1;role=$role;configuration=$Configuration;sourceRevision=(git rev-parse HEAD);workingTreeDirty=([bool](git status --porcelain));files=$files} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifest -Encoding UTF8
  }
  foreach($fixture in @('SimpleMemo','InputWindow')){
   dotnet publish "fixtures/$fixture/$fixture.csproj" -c $Configuration -r win-x64 --no-restore --self-contained false -o (Join-Path $target "test-apps/$fixture") --nologo
   if($LASTEXITCODE -ne 0){throw "Fixture publish failed: $fixture"}
  }
  Write-Output $target
 }
}finally{Pop-Location}
