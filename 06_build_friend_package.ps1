$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $repoRoot 'managed\PolinMegatranscriber.App\PolinMegatranscriber.App.csproj'
$nativeDll = Join-Path $repoRoot 'native\build\bin\pmtwhisper.dll'
$outRoot = Join-Path $repoRoot 'out'
$publishDir = Join-Path $outRoot 'Megatranscriber-win-x64'
$mediaDir = Join-Path $publishDir 'Runtime\MediaTools'
$thirdPartyDir = Join-Path $publishDir 'ThirdParty'

foreach ($required in @($project, $nativeDll)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Не найден обязательный файл: $required"
    }
}

function Find-MediaTools {
    $candidates = New-Object System.Collections.Generic.List[string]

    $configured = [Environment]::GetEnvironmentVariable('POLIN_MEGATRANSCRIBER_MEDIA_TOOLS')
    if (-not [string]::IsNullOrWhiteSpace($configured)) {
        $candidates.Add($configured.Trim().Trim('"'))
    }

    $candidates.Add('C:\ffmpeg')
    $candidates.Add('C:\ffmpeg\bin')

    $path = [Environment]::GetEnvironmentVariable('PATH')
    if (-not [string]::IsNullOrWhiteSpace($path)) {
        foreach ($entry in $path.Split([IO.Path]::PathSeparator)) {
            if (-not [string]::IsNullOrWhiteSpace($entry)) {
                $candidates.Add($entry.Trim().Trim('"'))
            }
        }
    }

    foreach ($directory in $candidates | Select-Object -Unique) {
        try {
            $ffmpeg = Join-Path $directory 'ffmpeg.exe'
            $ffprobe = Join-Path $directory 'ffprobe.exe'
            if ((Test-Path -LiteralPath $ffmpeg -PathType Leaf) -and
                (Test-Path -LiteralPath $ffprobe -PathType Leaf)) {
                return [pscustomobject]@{
                    Directory = $directory
                    FFmpeg = $ffmpeg
                    FFprobe = $ffprobe
                }
            }
        }
        catch {
            # Ignore malformed PATH entries and continue.
        }
    }

    throw 'Не удалось найти ffmpeg.exe и ffprobe.exe. Ожидались в C:\ffmpeg, C:\ffmpeg\bin, PATH или POLIN_MEGATRANSCRIBER_MEDIA_TOOLS.'
}

$tools = Find-MediaTools

Write-Host ''
Write-Host 'Источник FFmpeg:'
Write-Host "  $($tools.Directory)"

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

Write-Host ''
Write-Host 'Собираю self-contained win-x64...'

& dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $publishDir `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:TreatWarningsAsErrors=true

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish завершился с кодом $LASTEXITCODE"
}

# Native bridge copy is explicit: publish output must never rely on bin/Release.
Copy-Item -LiteralPath $nativeDll -Destination (Join-Path $publishDir 'pmtwhisper.dll') -Force

# Bundle FFmpeg/FFprobe exactly where WindowsMediaToolLocator looks first.
New-Item -ItemType Directory -Force -Path $mediaDir | Out-Null
Copy-Item -LiteralPath $tools.FFmpeg -Destination (Join-Path $mediaDir 'ffmpeg.exe') -Force
Copy-Item -LiteralPath $tools.FFprobe -Destination (Join-Path $mediaDir 'ffprobe.exe') -Force

# Preserve useful licensing/build provenance for the bundled FFmpeg.
New-Item -ItemType Directory -Force -Path $thirdPartyDir | Out-Null

$licenseCandidates = @(
    (Join-Path $tools.Directory 'LICENSE'),
    (Join-Path $tools.Directory 'LICENSE.txt'),
    (Join-Path (Split-Path -Parent $tools.Directory) 'LICENSE'),
    (Join-Path (Split-Path -Parent $tools.Directory) 'LICENSE.txt')
)

$copiedLicense = $false
foreach ($candidate in $licenseCandidates | Select-Object -Unique) {
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        Copy-Item -LiteralPath $candidate `
            -Destination (Join-Path $thirdPartyDir 'FFmpeg-LICENSE.txt') `
            -Force
        $copiedLicense = $true
        break
    }
}

if (-not $copiedLicense) {
    & $tools.FFmpeg -hide_banner -L 2>&1 |
        Out-File -LiteralPath (Join-Path $thirdPartyDir 'FFmpeg-LICENSE.txt') `
            -Encoding utf8
}

& $tools.FFmpeg -hide_banner -version 2>&1 |
    Out-File -LiteralPath (Join-Path $thirdPartyDir 'FFmpeg-BUILD.txt') `
        -Encoding utf8

$whisperLicense = Join-Path $repoRoot 'native\build\_deps\whisper_cpp-src\LICENSE'
if (Test-Path -LiteralPath $whisperLicense -PathType Leaf) {
    Copy-Item -LiteralPath $whisperLicense `
        -Destination (Join-Path $thirdPartyDir 'whisper.cpp-LICENSE.txt') `
        -Force
}

# Release assertions.
$requiredPublished = @(
    (Join-Path $publishDir 'PolinMegatranscriber.App.exe'),
    (Join-Path $publishDir 'pmtwhisper.dll'),
    (Join-Path $mediaDir 'ffmpeg.exe'),
    (Join-Path $mediaDir 'ffprobe.exe')
)

foreach ($required in $requiredPublished) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "В publish отсутствует обязательный файл: $required"
    }
}

$modelFiles = Get-ChildItem -LiteralPath $publishDir -Recurse -File -Filter 'ggml-*.bin'
if ($modelFiles.Count -gt 0) {
    throw 'В friend build неожиданно попали модели Whisper. Они должны скачиваться только при первом использовании.'
}

$allFiles = Get-ChildItem -LiteralPath $publishDir -Recurse -File
$totalBytes = ($allFiles | Measure-Object -Property Length -Sum).Sum
$totalMiB = [Math]::Round($totalBytes / 1MB, 1)

Write-Host ''
Write-Host 'Готово.'
Write-Host "  Publish: $publishDir"
Write-Host "  Файлов: $($allFiles.Count)"
Write-Host "  Размер: $totalMiB МБ"
Write-Host '  FFmpeg/FFprobe: встроены'
Write-Host '  .NET runtime: встроен (self-contained)'
Write-Host '  pmtwhisper.dll: встроен'
Write-Host '  Whisper models: НЕ встроены — скачиваются приложением при первом использовании'
Write-Host ''
Write-Host 'Следующий шаг после ручного запуска этого publish: .exe installer.'
