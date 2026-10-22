param([switch]$Test, [switch]$Installed, [switch]$Live)
$ErrorActionPreference='Stop'
$sdk=Join-Path (Split-Path $PSScriptRoot -Parent) '.dotnet\dotnet.exe'
if(-not(Test-Path -LiteralPath $sdk)){$sdk='dotnet'}
function Invoke-Dotnet([string[]]$Arguments){
    & $sdk @Arguments
    if($LASTEXITCODE){throw "dotnet failed: $($Arguments[0])"}
}
if($Test){
    $testArgs=@('run','--project',(Join-Path $PSScriptRoot 'GPTProxyBridge.Tests\GPTProxyBridge.Tests.csproj'),'-c','Release')
    if($Installed -or $Live){$testArgs+='--'}
    if($Installed){$testArgs+='--installed'}
    if($Live){$testArgs+='--live'}
    Invoke-Dotnet $testArgs
    Invoke-Dotnet @('build',(Join-Path $PSScriptRoot 'GPTProxyBridge.LaunchFixture\GPTProxyBridge.LaunchFixture.csproj'),'-c','Release')
    $oldRoot=$env:DOTNET_ROOT
    $oldFixture=$env:GPT_PB_FIXTURE_EXE
    try{
        $sdkPath=(Get-Command $sdk).Source
        $env:DOTNET_ROOT=Split-Path $sdkPath -Parent
        $env:GPT_PB_FIXTURE_EXE=Join-Path $PSScriptRoot 'GPTProxyBridge.LaunchFixture\bin\Release\net10.0\GPTProxyBridge.LaunchFixture.exe'
        Invoke-Dotnet @('run','--project',(Join-Path $PSScriptRoot 'GPTProxyBridge.UiTests\GPTProxyBridge.UiTests.csproj'),'-c','Release')
    }finally{$env:DOTNET_ROOT=$oldRoot;$env:GPT_PB_FIXTURE_EXE=$oldFixture}
}
$project=Join-Path $PSScriptRoot 'GPTProxyBridge.App\GPTProxyBridge.App.csproj'
[xml]$metadata=Get-Content -LiteralPath $project -Raw
$version=$metadata.Project.PropertyGroup.Version
$output=Join-Path $PSScriptRoot "release\publish-$version"
Invoke-Dotnet @('publish',$project,'-c','Release','-r','win-x64','--self-contained','true','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:DebugType=None','-p:DebugSymbols=false','-o',$output)
$package=Join-Path $PSScriptRoot "release\GPT-Proxy-Bridge-$version-win-x64"
New-Item -ItemType Directory -Path $package -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $output 'GPT Proxy Bridge.exe') -Destination $package -Force
foreach($file in @('README.md','LICENSE','THIRD-PARTY-NOTICES.md','VERIFICATION.md','CHANGELOG.md')){
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $package -Force
}
$zip=Join-Path $PSScriptRoot "release\GPT-Proxy-Bridge-$version-win-x64.zip"
Compress-Archive -LiteralPath $package -DestinationPath $zip -Force
$hash=Get-FileHash -LiteralPath $zip -Algorithm SHA256
"$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'release\SHA256SUMS.txt') -Encoding ascii
Write-Output $zip
