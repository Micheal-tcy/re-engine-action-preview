$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/ReExtractor.Gui/ReExtractor.Gui.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$version = [string]$projectXml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be X.Y.Z' }
if ($env:GITHUB_REF_TYPE -eq 'tag' -and $env:GITHUB_REF_NAME -ne "v$version") { throw 'Tag and project version differ' }
$dist = Join-Path $repoRoot 'dist'
$build = Join-Path $dist ('build-' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $dist ('package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dist,$build,$package -Force | Out-Null
& dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $build
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
$exeName = 'ReExtractor-ActionPreview.exe'
$builtExe = Join-Path $build $exeName
$actual = [Diagnostics.FileVersionInfo]::GetVersionInfo($builtExe).FileVersion
if ($actual -ne "$version.0") { throw "Wrong EXE version: $actual" }
Copy-Item -LiteralPath $builtExe -Destination (Join-Path $dist $exeName) -Force
Copy-Item -LiteralPath $builtExe -Destination $package
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md') -Destination $package
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSES') -Destination $package -Recurse
$zipName = "ReExtractor-ActionPreview-v$version-win-x64.zip"
$zipPath = Join-Path $dist $zipName
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zipPath -Force
$lines = @($zipName,$exeName) | ForEach-Object { ((Get-FileHash -LiteralPath (Join-Path $dist $_) -Algorithm SHA256).Hash.ToLowerInvariant()) + '  ' + $_ }
[IO.File]::WriteAllLines((Join-Path $dist 'SHA256SUMS.txt'),$lines,[Text.UTF8Encoding]::new($false))
Write-Host "Created $zipName and standalone $exeName"
