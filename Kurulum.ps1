param([switch]$Kaldir)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$hostName = 'com.tektik.video'
$extensionId = 'kdecmlbnjjcfjipibclhfkclhkjmbmjo'
$installRoot = Join-Path $env:LOCALAPPDATA 'TekTikVideo'
$toolsPath = Join-Path $installRoot 'tools'
$manifestPath = Join-Path $installRoot "$hostName.json"
$hostPath = Join-Path $installRoot 'TekTikVideo.Host.exe'
$statePath = Join-Path $installRoot 'tools-state.json'
$registryPaths = @(
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName",
    "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$hostName"
)

function Get-VerifiedReleaseAsset($Repository, $Pattern) {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/releases/latest" `
        -Headers @{ 'User-Agent' = 'TekTikVideo-Setup' } -TimeoutSec 30
    $assets = @($release.assets | Where-Object { $_.name -match $Pattern })
    if ($assets.Count -ne 1) {
        throw "$Repository sürümünde beklenen Windows dosyası bulunamadı."
    }
    $asset = $assets[0]
    $prefix = "https://github.com/$Repository/releases/download/"
    if (!$asset.browser_download_url.StartsWith($prefix, [StringComparison]::Ordinal) -or
        $asset.digest -notmatch '^sha256:([a-f0-9]{64})$') {
        throw "$Repository için geçerli indirme adresi veya SHA-256 bilgisi bulunamadı."
    }
    [pscustomobject]@{ Url = $asset.browser_download_url; Sha256 = $Matches[1] }
}

function Save-ReleaseTools($Package, $Asset, $Staging, $Previous) {
    $files = @($Package.Files)
    if ($Previous -and $Previous.release -eq $Asset.Sha256) {
        $intact = $true
        foreach ($file in $files) {
            $installedFile = Join-Path $toolsPath $file
            if (!(Test-Path -LiteralPath $installedFile -PathType Leaf) -or
                (Get-FileHash -LiteralPath $installedFile).Hash -ne $Previous.files.$file) {
                $intact = $false
                break
            }
        }
        if ($intact) {
            Write-Host "  $($Package.Name) güncel."
            return $Previous
        }
    }

    Write-Host "  $($Package.Name) indiriliyor…"
    $downloadPath = Join-Path $Staging $Package.Name
    Invoke-WebRequest -UseBasicParsing -Uri $Asset.Url -OutFile $downloadPath -TimeoutSec 360
    if ((Get-FileHash -LiteralPath $downloadPath).Hash -ne $Asset.Sha256) {
        throw "$($Package.Name) için SHA-256 doğrulaması başarısız."
    }

    if ($Package.Archive) {
        $archive = [IO.Compression.ZipFile]::OpenRead($downloadPath)
        try {
            foreach ($file in $files) {
                $entries = @($archive.Entries | Where-Object Name -EQ $file)
                if ($entries.Count -ne 1) { throw "$($Package.Name) içinde $file bulunamadı." }
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], (Join-Path $Staging $file))
            }
        } finally {
            $archive.Dispose()
        }
    } else {
        Move-Item -LiteralPath $downloadPath -Destination (Join-Path $Staging $files[0])
    }

    $hashes = @{}
    foreach ($file in $files) {
        $hashes[$file] = (Get-FileHash -LiteralPath (Join-Path $Staging $file)).Hash
    }
    @{ release = $Asset.Sha256; files = $hashes }
}

$staging = $null
try {
    if ($Kaldir) {
        foreach ($key in $registryPaths) {
            if ((Test-Path -LiteralPath $key) -and (Get-Item -LiteralPath $key).GetValue('') -eq $manifestPath) {
                Remove-Item -LiteralPath $key
            }
        }
        foreach ($path in @($hostPath, $manifestPath, $statePath)) {
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
        }
        foreach ($file in @('yt-dlp.exe', 'deno.exe', 'ffmpeg.exe', 'ffprobe.exe')) {
            $path = Join-Path $toolsPath $file
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
        }
        foreach ($path in @($toolsPath, $installRoot)) {
            if ((Test-Path -LiteralPath $path) -and !(Get-ChildItem -LiteralPath $path -Force)) {
                Remove-Item -LiteralPath $path
            }
        }
        Write-Host 'Yerel yardımcı kaldırıldı. Chrome veya Edge içinden TekTık Video eklentisini kaldırabilirsin.'
        exit 0
    }

    if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
        throw 'Bu kurulum Windows x64 için hazırlandı.'
    }
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (!(Test-Path -LiteralPath $csc -PathType Leaf)) { throw '.NET Framework 4 derleyicisi bulunamadı.' }

    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $staging = [IO.Path]::GetFullPath((Join-Path $tempRoot ('TekTikVideo-' + [guid]::NewGuid().ToString('N'))))
    New-Item -ItemType Directory -Path $staging | Out-Null
    $stagedHost = Join-Path $staging 'TekTikVideo.Host.exe'
    & $csc /nologo /optimize+ /target:winexe /codepage:65001 /r:System.Web.Extensions.dll "/out:$stagedHost" (Join-Path $PSScriptRoot 'native\Host.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Yerel yardımcı derlenemedi.' }

    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $packages = @(
        @{ Name = 'yt-dlp'; Repo = 'yt-dlp/yt-dlp'; Pattern = '^yt-dlp\.exe$'; Files = @('yt-dlp.exe') },
        @{ Name = 'Deno'; Repo = 'denoland/deno'; Pattern = '^deno-x86_64-pc-windows-msvc\.zip$'; Files = @('deno.exe'); Archive = $true },
        @{ Name = 'FFmpeg'; Repo = 'GyanD/codexffmpeg'; Pattern = '^ffmpeg-[\d.]+-essentials_build\.zip$'; Files = @('ffmpeg.exe', 'ffprobe.exe'); Archive = $true }
    )
    $previous = $null
    if (Test-Path -LiteralPath $statePath) {
        try { $previous = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json } catch { $previous = $null }
    }

    Write-Host 'TekTık Video bileşenleri hazırlanıyor…'
    $state = @{}
    foreach ($package in $packages) {
        $asset = Get-VerifiedReleaseAsset $package.Repo $package.Pattern
        $name = $package.Name
        $state[$name] = Save-ReleaseTools $package $asset $staging $previous.$name
    }

    New-Item -ItemType Directory -Force -Path $toolsPath | Out-Null
    foreach ($package in $packages) {
        foreach ($file in $package.Files) {
            $stagedFile = Join-Path $staging $file
            if (Test-Path -LiteralPath $stagedFile) {
                Copy-Item -LiteralPath $stagedFile -Destination (Join-Path $toolsPath $file) -Force
            }
        }
    }
    Copy-Item -LiteralPath $stagedHost -Destination $hostPath -Force
    $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statePath -Encoding UTF8

    $manifest = @{
        name = $hostName
        description = 'TekTik Video local download host'
        path = $hostPath
        type = 'stdio'
        allowed_origins = @("chrome-extension://$extensionId/")
    } | ConvertTo-Json
    [IO.File]::WriteAllText($manifestPath, $manifest, [Text.UTF8Encoding]::new($false))
    foreach ($key in $registryPaths) {
        New-Item -Path $key -Force | Out-Null
        Set-Item -LiteralPath $key -Value $manifestPath
    }

    Write-Host ''
    Write-Host 'Yerel yardımcı hazır. Chrome veya Edge uzantılar sayfasında geliştirici modunu aç.'
    Write-Host 'Paketlenmemiş öğe yükle seçeneğiyle şu klasörü seç:'
    Write-Host (Join-Path $PSScriptRoot 'extension')
} catch {
    Write-Host "Kurulum tamamlanamadı. $($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally {
    if ($staging -and $staging.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $staging) -match '^TekTikVideo-[0-9a-f]{32}$' -and (Test-Path -LiteralPath $staging)) {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}
