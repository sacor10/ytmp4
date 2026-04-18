param(
    [string]$Version = "1.0"
)

$ErrorActionPreference = "Stop"

$project    = "YtMp4\YtMp4.csproj"
$publishDir = "dist\app"
$zipName    = "ytmp4-v$Version.zip"
$zipPath    = "dist\$zipName"

Write-Host "Building YtMp4 v$Version..." -ForegroundColor Cyan

if (Test-Path "dist") { Remove-Item "dist" -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Copy-Item -Path "YtMp4\tools" -Destination $publishDir -Recurse -Force

@"
YtMp4 - YouTube to MP4 Downloader
===================================
1. Extract this zip to any folder
2. Run YtMp4.exe
3. Paste a YouTube URL, pick a save folder, click Download

No installation needed. Windows 10/11 x64 only.
"@ | Out-File -FilePath "$publishDir\README.txt" -Encoding utf8

Write-Host "Zipping..." -ForegroundColor Cyan
Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath -Force

$sizeMB = [math]::Round((Get-Item $zipPath).Length / 1MB, 0)
Write-Host ""
Write-Host "Done!  dist\$zipName  ($sizeMB MB)" -ForegroundColor Green
Write-Host "Upload it to sacor.xyz and link to:  https://sacor.xyz/ytmp4/$zipName"
