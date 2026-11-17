param([string]$Version = '0.0.1', [string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Versao invalida.' }
if ($Runtime -ne 'win-x64') { throw 'Runtime nao suportado pelo pacote.' }
$Root = Split-Path $PSScriptRoot -Parent
$Output = Join-Path $Root '.artifacts/app'
$Portable = Join-Path $Root '.artifacts/portable'
$Legal = Join-Path $Root '.artifacts/licenses'
foreach ($Directory in @($Output, $Portable, $Legal)) {
    if (Test-Path $Directory) { Remove-Item $Directory -Recurse -Force }
    New-Item $Directory -ItemType Directory -Force | Out-Null
}
$Project = Join-Path $Root 'src/Krosforge.Launcher/Krosforge.Launcher.csproj'
dotnet restore $Project -r $Runtime
if ($LASTEXITCODE -ne 0) { throw 'Falha ao restaurar dependencias.' }
Copy-Item (Join-Path $Root 'LICENSE') $Legal
Copy-Item (Join-Path $Root 'THIRD_PARTY_NOTICES.md') $Legal
$NugetRoot = ((dotnet nuget locals global-packages --list) -replace '^global-packages: ', '').Trim()
foreach ($File in Get-ChildItem $NugetRoot -Recurse -File | Where-Object { $_.Name -match '^(LICENSE|LICENCE|NOTICE|THIRD-PARTY-NOTICES)([.-].*)?$' }) {
    $Target = Join-Path $Legal ([IO.Path]::GetRelativePath($NugetRoot, $File.FullName))
    New-Item (Split-Path $Target -Parent) -ItemType Directory -Force | Out-Null
    Copy-Item $File.FullName $Target
}
$LicenseArchive = Join-Path $Root '.artifacts/licenses.zip'
Compress-Archive "$Legal/*" $LicenseArchive -Force
dotnet publish $Project -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:DebugType=embedded "-p:BundledLicensesFile=$LicenseArchive" "-p:Version=$Version" -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar aplicativo.' }
$Files = @(Get-ChildItem $Output -File -Recurse)
if ($Files.Count -ne 1 -or $Files[0].Name -ne 'Krosforge.exe') { throw 'O pacote precisa conter somente Krosforge.exe.' }
Copy-Item (Join-Path $Output 'Krosforge.exe') $Portable
$Archive = Join-Path $Root ".artifacts/Krosforge-$Version-win-x64-portable.zip"
Compress-Archive (Join-Path $Portable 'Krosforge.exe') $Archive -Force
