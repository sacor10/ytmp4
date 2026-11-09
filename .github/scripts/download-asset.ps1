param(
    [Parameter(Mandatory=$true)][string]$Repo,
    [Parameter(Mandatory=$true)][string]$Pattern,
    [string]$OutDir = ".",
    [string]$Tag = "",
    [string]$FallbackUrl = "",
    [int]$MaxRetries = 5
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $OutDir)) {
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
}

$success = $false
$targetPath = Join-Path $OutDir $Pattern

for ($attempt = 1; $attempt -le $MaxRetries; $attempt++) {
    Write-Host "=== Download attempt $attempt of $MaxRetries for '$Pattern' from '$Repo' ===" -ForegroundColor Cyan

    # Ensure clean state for target file before attempt if corrupted or empty
    if ((Test-Path $targetPath) -and (Get-Item $targetPath).Length -eq 0) {
        Remove-Item -Force $targetPath -ErrorAction SilentlyContinue
    }

    # 1. Primary method: GitHub CLI (uses GitHub REST API & direct blob storage, avoiding 504 frontend timeouts)
    $hasGh = [bool](Get-Command gh -ErrorAction SilentlyContinue)
    if ($hasGh) {
        try {
            $ghArgs = @("release", "download")
            if ($Tag) {
                $ghArgs += $Tag
            }
            $ghArgs += @("--repo", $Repo, "--pattern", $Pattern, "--dir", $OutDir, "--clobber")

            Write-Host "Executing: gh $($ghArgs -join ' ')"
            $prevEA = $ErrorActionPreference
            $ErrorActionPreference = "Continue"
            & gh @ghArgs
            $exitCode = $LASTEXITCODE
            $ErrorActionPreference = $prevEA

            if ($exitCode -eq 0) {
                $matchedFiles = Get-ChildItem -Path $OutDir -Filter $Pattern -File -ErrorAction SilentlyContinue
                if ($matchedFiles -and $matchedFiles.Count -gt 0 -and $matchedFiles[0].Length -gt 0) {
                    $sizeMB = [math]::Round($matchedFiles[0].Length / 1MB, 2)
                    Write-Host "Successfully downloaded '$($matchedFiles[0].Name)' via gh CLI ($sizeMB MB)" -ForegroundColor Green
                    $success = $true
                    break
                } else {
                    Write-Warning "gh command exited with code 0 but file '$Pattern' was not found or is 0 bytes."
                }
            } else {
                Write-Warning "gh release download failed with exit code $exitCode."
            }
        } catch {
            Write-Warning "gh execution encountered an exception: $_"
        }
    }

    # 2. Query GitHub REST API directly to resolve static release asset URL
    # (Static asset URLs bypass GitHub frontend /releases/latest/download/ web redirects that return 504 Gateway Time-out)
    $resolvedUrl = ""
    if (-not $success) {
        try {
            $apiEndpoint = if ($Tag) {
                "https://api.github.com/repos/$Repo/releases/tags/$Tag"
            } else {
                "https://api.github.com/repos/$Repo/releases/latest"
            }
            $headers = @{ "User-Agent" = "ytmp4-downloader" }
            $token = if ($env:GH_TOKEN) { $env:GH_TOKEN } elseif ($env:GITHUB_TOKEN) { $env:GITHUB_TOKEN } else { "" }
            if ($token) {
                $headers["Authorization"] = "Bearer $token"
            }
            Write-Host "Resolving asset URL via GitHub REST API: $apiEndpoint"
            $prevEA = $ErrorActionPreference
            $ErrorActionPreference = "Stop"
            $rel = Invoke-RestMethod -Uri $apiEndpoint -Headers $headers -TimeoutSec 15
            $ErrorActionPreference = $prevEA
            if ($rel -and $rel.assets) {
                $matchedAsset = $rel.assets | Where-Object { $_.name -like $Pattern } | Select-Object -First 1
                if ($matchedAsset -and $matchedAsset.browser_download_url) {
                    $resolvedUrl = $matchedAsset.browser_download_url
                    Write-Host "Resolved static asset URL from API: $resolvedUrl ($([math]::Round($matchedAsset.size / 1MB, 2)) MB)" -ForegroundColor Cyan
                }
            }
        } catch {
            Write-Warning "Could not resolve asset via GitHub REST API: $_"
        }
    }

    $urlToTry = if ($resolvedUrl) { $resolvedUrl } else { $FallbackUrl }

    # 3. Secondary method: curl.exe with retries and redirect following
    $hasCurl = [bool](Get-Command curl.exe -ErrorAction SilentlyContinue)
    if (-not $success -and $urlToTry -and $hasCurl) {
        Write-Host "Attempting download via curl.exe from '$urlToTry'..." -ForegroundColor Yellow
        try {
            $curlArgs = @(
                "-fL",
                "-sS",
                "--retry", "5",
                "--retry-delay", "3",
                "--retry-all-errors",
                "--connect-timeout", "30",
                "--max-time", "600",
                "-o", $targetPath,
                $urlToTry
            )
            Write-Host "Executing: curl.exe -fL -sS --retry 5 --retry-delay 3 --retry-all-errors --connect-timeout 30 --max-time 600 -o $targetPath $urlToTry"
            $prevEA = $ErrorActionPreference
            $ErrorActionPreference = "Continue"
            & curl.exe @curlArgs
            $exitCode = $LASTEXITCODE
            $ErrorActionPreference = $prevEA

            if ($exitCode -eq 0 -and (Test-Path $targetPath) -and (Get-Item $targetPath).Length -gt 0) {
                $sizeMB = [math]::Round((Get-Item $targetPath).Length / 1MB, 2)
                Write-Host "Successfully downloaded '$Pattern' via curl fallback ($sizeMB MB)" -ForegroundColor Green
                $success = $true
                break
            } else {
                Write-Warning "curl failed with exit code $exitCode."
            }
        } catch {
            Write-Warning "curl execution encountered an exception: $_"
        }
    }

    # 4. Tertiary method: Invoke-WebRequest with timeout and retries
    if (-not $success -and $urlToTry) {
        Write-Host "Attempting fallback via Invoke-WebRequest..." -ForegroundColor Yellow
        try {
            $iwrParams = @{
                Uri = $urlToTry
                OutFile = $targetPath
                TimeoutSec = 120
            }
            if ($PSVersionTable.PSVersion.Major -lt 6) {
                $iwrParams["UseBasicParsing"] = $true
            }
            if ($PSVersionTable.PSVersion.Major -ge 7) {
                $iwrParams["MaximumRetryCount"] = 3
                $iwrParams["RetryIntervalSec"] = 3
            }
            Invoke-WebRequest @iwrParams
            if ((Test-Path $targetPath) -and (Get-Item $targetPath).Length -gt 0) {
                $sizeMB = [math]::Round((Get-Item $targetPath).Length / 1MB, 2)
                Write-Host "Successfully downloaded '$Pattern' via Invoke-WebRequest ($sizeMB MB)" -ForegroundColor Green
                $success = $true
                break
            }
        } catch {
            Write-Warning "Invoke-WebRequest failed: $_"
        }
    }

    if (-not $success) {
        # Clean up any partial download
        if (Test-Path $targetPath) {
            Remove-Item -Force $targetPath -ErrorAction SilentlyContinue
        }
        if ($attempt -lt $MaxRetries) {
            $sleepSec = [math]::Min(30, [math]::Pow(2, $attempt))
            Write-Host "Download attempt $attempt failed. Retrying in $sleepSec seconds..." -ForegroundColor DarkYellow
            Start-Sleep -Seconds $sleepSec
        }
    }
}

if (-not $success) {
    throw "Failed to download '$Pattern' from '$Repo' after $MaxRetries attempts."
}
