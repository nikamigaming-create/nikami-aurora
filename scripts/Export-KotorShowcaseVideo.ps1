[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputPath,

    [string]$Manifest,
    [string]$Godot,
    [string]$OpenXRRuntimeJson,

    [ValidateRange(24, 120)]
    [int]$FramesPerSecond = 60,

    [string]$Ffmpeg = 'ffmpeg',
    [string]$Ffprobe = 'ffprobe'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputPath)
if ([IO.Path]::GetExtension($output) -ine '.mp4') {
    throw 'The final showcase output must use the .mp4 extension.'
}
if (Test-Path -LiteralPath $output) {
    throw "Refusing to overwrite an existing showcase video: $output"
}

$ffmpegCommand = Get-Command $Ffmpeg -ErrorAction SilentlyContinue
$ffprobeCommand = Get-Command $Ffprobe -ErrorAction SilentlyContinue
if (-not $ffmpegCommand -or -not $ffprobeCommand) {
    throw 'ffmpeg and ffprobe are required for final MP4 validation.'
}

$outputDirectory = Split-Path -Parent $output
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$temporaryName = 'nikami-aurora-showcase-' + [Guid]::NewGuid().ToString('N')
$temporaryDirectory = [IO.Path]::GetFullPath(
    (Join-Path $temporaryRoot $temporaryName))
if (-not $temporaryDirectory.StartsWith(
        $temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($temporaryDirectory) -notlike 'nikami-aurora-showcase-*') {
    throw "Unsafe temporary showcase directory: $temporaryDirectory"
}

New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
$intermediate = Join-Path $temporaryDirectory 'godot-showcase.ogv'
$encoded = Join-Path $temporaryDirectory 'nikami-aurora-kotor-showcase.mp4'
$godotStdoutLog = Join-Path $temporaryDirectory 'godot-stdout.log'
$godotStderrLog = Join-Path $temporaryDirectory 'godot-stderr.log'
$completed = $false

try {
    $launch = @{
        OpenXRSimulator = $true
        ShowcaseRoute = $true
        ExitOnShowcaseComplete = $true
        CleanCapture = $true
        MoviePath = $intermediate
        MovieFps = $FramesPerSecond
        GodotStdoutPath = $godotStdoutLog
        GodotStderrPath = $godotStderrLog
    }
    if (-not [string]::IsNullOrWhiteSpace($Manifest)) {
        $launch.Manifest = $Manifest
    }
    if (-not [string]::IsNullOrWhiteSpace($Godot)) {
        $launch.Godot = $Godot
    }
    if (-not [string]::IsNullOrWhiteSpace($OpenXRRuntimeJson)) {
        $launch.OpenXRRuntimeJson = $OpenXRRuntimeJson
    }

    & (Join-Path $PSScriptRoot 'Start-KotorGodot.ps1') @launch
    if ($LASTEXITCODE -ne 0) {
        throw "Godot showcase recording failed with exit code $LASTEXITCODE"
    }
    if (-not (Test-Path -LiteralPath $intermediate -PathType Leaf) -or
        (Get-Item -LiteralPath $intermediate).Length -lt 1MB) {
        throw 'Godot Movie Maker did not produce a usable intermediate.'
    }

    $consoleText = (Get-Content -LiteralPath $godotStdoutLog -Raw) +
                   [Environment]::NewLine +
                   (Get-Content -LiteralPath $godotStderrLog -Raw)
    $shutdownMarker = 'NIKAMI_AURORA_OPENXR status=shutdown-requested boundary=frame-post-draw'
    $shutdownIndex = $consoleText.IndexOf(
        $shutdownMarker, [StringComparison]::Ordinal)
    if ($shutdownIndex -lt 0) {
        throw 'Godot console did not report its post-draw OpenXR shutdown request.'
    }
    $allowedTeardownErrors = @(
        "^ERROR: 2 RID allocations of type 'N9OpenXRAPI18InteractionProfileE' were leaked at exit\.$",
        "^ERROR: Attempt to disconnect a nonexistent connection from '<OpenXRSpatialEntityExtension#[0-9]+>'\. Signal: 'spatial_discovery_recommended', callable: 'OpenXRSpatialMarkerTrackingCapability::_on_spatial_discovery_recommended'\.$"
    )
    $consoleErrors = [regex]::Matches(
        $consoleText, '(?m)^ERROR:.*$') | ForEach-Object {
            $_.Value.TrimEnd([char[]]"`r")
        }
    foreach ($consoleError in $consoleErrors) {
        $allowed = $false
        foreach ($pattern in $allowedTeardownErrors) {
            if ($consoleError -match $pattern) {
                $allowed = $true
                break
            }
        }
        if (-not $allowed -or
            $consoleText.IndexOf($consoleError, [StringComparison]::Ordinal) -lt
                $shutdownIndex) {
            throw "Unexpected Godot console error: $consoleError"
        }
    }
    if ($consoleErrors.Count -ne 2) {
        throw "Expected exactly two allowlisted Godot teardown diagnostics, " +
              "found $($consoleErrors.Count)."
    }

    $runtimeLog = Join-Path $env:APPDATA `
        'Godot\app_userdata\Nikami Aurora\logs\godot.log'
    if (-not (Test-Path -LiteralPath $runtimeLog -PathType Leaf)) {
        throw 'Godot runtime log is missing after showcase recording.'
    }
    $runtimeText = Get-Content -LiteralPath $runtimeLog -Raw
    $requiredTelemetry = @(
        'NIKAMI_AURORA_OPENXR status=ready',
        'spectator=True',
        'NIKAMI_AURORA_SHOWCASE_TRANSMISSION status=pass',
        'NIKAMI_AURORA_FIRST_ENCOUNTER status=pass',
        ('NIKAMI_AURORA_XR_LOCAL_AVATAR status=gameplay-head-hidden ' +
         'headMeshes=8 bodyMeshes=3 hands=left,right weapon=present'),
        'NIKAMI_AURORA_SHOWCASE status=pass'
    )
    foreach ($required in $requiredTelemetry) {
        if (-not $runtimeText.Contains($required, [StringComparison]::Ordinal)) {
            throw "Showcase runtime telemetry is missing: $required"
        }
    }
    if ($runtimeText -match 'ERROR:|status=fail|fallback=desktop') {
        throw 'Showcase runtime log contains an error, failure, or XR fallback.'
    }

    & $ffmpegCommand.Source -nostdin -hide_banner -loglevel error `
        -i $intermediate -map '0:v:0' -map '0:a:0' `
        -c:v libx264 -preset slow -crf 18 -pix_fmt yuv420p `
        -c:a aac -b:a 192k -movflags '+faststart' $encoded
    if ($LASTEXITCODE -ne 0) {
        throw "ffmpeg failed with exit code $LASTEXITCODE"
    }

    $probeJson = (& $ffprobeCommand.Source -v error -show_streams `
        -show_format -of json $encoded) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) {
        throw "ffprobe failed with exit code $LASTEXITCODE"
    }
    $probe = $probeJson | ConvertFrom-Json
    $video = @($probe.streams | Where-Object codec_type -eq 'video')
    $audio = @($probe.streams | Where-Object codec_type -eq 'audio')
    $duration = [double]::Parse(
        [string]$probe.format.duration,
        [Globalization.CultureInfo]::InvariantCulture)
    if ($video.Count -ne 1 -or $audio.Count -lt 1 -or
        [int]$video[0].width -ne 1280 -or [int]$video[0].height -ne 720 -or
        $duration -lt 90.0 -or $duration -gt 150.0) {
        throw "Final MP4 validation failed: video=$($video.Count) audio=$($audio.Count) " +
              "size=$($video[0].width)x$($video[0].height) duration=$duration"
    }

    Move-Item -LiteralPath $encoded -Destination $output
    $result = [ordered]@{
        path = $output
        sha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
        byteCount = (Get-Item -LiteralPath $output).Length
        durationSeconds = [Math]::Round($duration, 3)
        width = [int]$video[0].width
        height = [int]$video[0].height
        videoCodec = [string]$video[0].codec_name
        audioCodec = [string]$audio[0].codec_name
        framesPerSecond = $FramesPerSecond
        allowlistedGodotTeardownDiagnostics = $consoleErrors.Count
    }
    $completed = $true
    $result | ConvertTo-Json
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        $resolvedTemporary = [IO.Path]::GetFullPath($temporaryDirectory)
        if (-not $resolvedTemporary.StartsWith(
                $temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($resolvedTemporary) -notlike
                'nikami-aurora-showcase-*') {
            throw "Refusing to remove unsafe temporary directory: $resolvedTemporary"
        }
        Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
    }
    if (-not $completed -and (Test-Path -LiteralPath $output)) {
        Remove-Item -LiteralPath $output -Force
    }
}
