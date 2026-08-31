[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputPath,

    [string]$Manifest,
    [Parameter(Mandatory)]
    [ValidateCount(2, 8)]
    [string[]]$GenericManifests,
    [string]$Godot,
    [string]$OpenXRRuntimeJson,

    [ValidateSet('Desktop', 'OpenXRSimulator')]
    [string]$Presentation = 'Desktop',

    [ValidateRange(24, 120)]
    [int]$FramesPerSecond = 60,

    [string]$Ffmpeg = 'ffmpeg',
    [string]$Ffprobe = 'ffprobe'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

function Assert-EnhancedKotorPbrCoverage {
    param(
        [Parameter(Mandatory)][string]$Evidence,
        [Parameter(Mandatory)][string]$ModuleId
    )
    $applicationMarker =
        'NIKAMI_AURORA_RENDER_QUALITY status=ready scope=application ' +
        'tier=enhanced backend=forward_plus'
    if ($Evidence.IndexOf(
            $applicationMarker, [StringComparison]::Ordinal) -lt 0) {
        throw "Application-wide enhanced render quality is missing for $ModuleId."
    }
    foreach ($scope in @('ROOM', 'DYNAMIC')) {
        $pattern =
            "(?m)^NIKAMI_AURORA_${scope}_PBR status=ready " +
            "module=$([regex]::Escape($ModuleId)) tier=enhanced " +
            'renderable_surfaces=(?<renderable>\d+) ' +
            'source_unshaded_surfaces=(?<unshaded>\d+) ' +
            'pbr_eligible_surfaces=(?<eligible>\d+) ' +
            'pbr_surfaces=(?<pbr>\d+)\b'
        $match = [regex]::Match($Evidence, $pattern)
        if (-not $match.Success) {
            throw "Enhanced $scope PBR telemetry is missing for $ModuleId."
        }
        $renderable = [int]$match.Groups['renderable'].Value
        $unshaded = [int]$match.Groups['unshaded'].Value
        $eligible = [int]$match.Groups['eligible'].Value
        $pbr = [int]$match.Groups['pbr'].Value
        if ($renderable -le 0 -or $unshaded -lt 0 -or
            $eligible -ne $renderable - $unshaded -or $pbr -ne $eligible) {
            throw "Incomplete $scope PBR coverage for ${ModuleId}: " +
                  "renderable=$renderable unshaded=$unshaded " +
                  "eligible=$eligible pbr=$pbr"
        }
    }
}

$resolvedGenericManifests = @()
$genericModules = @()
foreach ($genericManifest in $GenericManifests) {
    $resolved = [IO.Path]::GetFullPath($genericManifest)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Generic KOTOR showcase manifest was not found: $resolved"
    }
    $contract = Get-Content -LiteralPath $resolved -Raw | ConvertFrom-Json
    if ($contract.schema -ne 'nikami-aurora-kotor-module-v1' -or
        $contract.profileId -ne 'kotor' -or
        $contract.contentMode -ne 'generic-world' -or
        [string]$contract.module -notmatch '^[A-Za-z0-9_]{1,16}$') {
        throw "Invalid generic KOTOR showcase manifest: $resolved"
    }
    $module = ([string]$contract.module).ToLowerInvariant()
    if ($genericModules -contains $module) {
        throw "Duplicate generic KOTOR showcase module: $module"
    }
    $resolvedGenericManifests += $resolved
    $genericModules += $module
}
if ($Presentation -ne 'Desktop') {
    throw 'The multi-area cinematic exporter currently requires Desktop presentation.'
}
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
$concatList = Join-Path $temporaryDirectory 'concat.txt'
$godotStdoutLog = Join-Path $temporaryDirectory 'godot-stdout.log'
$godotStderrLog = Join-Path $temporaryDirectory 'godot-stderr.log'
$genericIntermediates = @()
$normalizedClips = @()
$completed = $false

try {
    $launch = @{
        ShowcaseRoute = $true
        ExitOnShowcaseComplete = $true
        CleanCapture = $true
        MoviePath = $intermediate
        MovieFps = $FramesPerSecond
        GodotStdoutPath = $godotStdoutLog
        GodotStderrPath = $godotStderrLog
        TimeoutSeconds = 1800
    }
    if ($Presentation -eq 'OpenXRSimulator') {
        $launch.OpenXRSimulator = $true
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
    $consoleErrors = [regex]::Matches(
        $consoleText, '(?m)^ERROR:.*$') | ForEach-Object {
            $_.Value.TrimEnd([char[]]"`r")
        }
    if ($Presentation -eq 'OpenXRSimulator') {
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
    }
    elseif ($consoleErrors.Count -ne 0) {
        throw "Desktop recording reported a Godot console error: " +
              $consoleErrors[0]
    }

    $runtimeLog = Join-Path $env:APPDATA `
        'Godot\app_userdata\Nikami Aurora\logs\godot.log'
    if (-not (Test-Path -LiteralPath $runtimeLog -PathType Leaf)) {
        throw 'Godot runtime log is missing after showcase recording.'
    }
    $runtimeText = Get-Content -LiteralPath $runtimeLog -Raw
    $requiredTelemetry = @(
        ('NIKAMI_AURORA_RENDER_PIPELINE status=ready method=forward_plus ' +
         'tier=enhanced tonemap=agx ssao=1 ssil=1 ssr=1 sdfgi=0 ' +
         'volumetric_fog=0 glow=1'),
        ('NIKAMI_AURORA_LIGHTMAP_TRANSFER status=ready tier=enhanced ' +
         'formula=baked-preserving-bounded-dynamic diffuse_weight=0.12 ' +
         'baked_weight=1.00 dynamic_ambient_weight=0.15 dynamic_lights=1 ' +
         'double_light=bounded'),
        ('NIKAMI_AURORA_ROOM_EMITTERS status=ready module=end_m01aa ' +
         'authored=12 materialized=12 alpha=9 additive=3 single=0'),
        'NIKAMI_AURORA_SHOWCASE_TRANSMISSION status=pass',
        'NIKAMI_AURORA_FIRST_ENCOUNTER status=pass',
        'NIKAMI_AURORA_SHOWCASE status=pass'
    )
    if ($Presentation -eq 'OpenXRSimulator') {
        $requiredTelemetry += @(
            'NIKAMI_AURORA_OPENXR status=ready',
            'spectator=True',
            ('NIKAMI_AURORA_XR_LOCAL_AVATAR status=gameplay-head-hidden ' +
             'headMeshes=8 bodyMeshes=3 hands=left,right weapon=present')
        )
    }
    else {
        $requiredTelemetry += 'NIKAMI_AURORA_OPENXR status=disabled'
    }
    foreach ($required in $requiredTelemetry) {
        if (-not $runtimeText.Contains($required, [StringComparison]::Ordinal)) {
            throw "Showcase runtime telemetry is missing: $required"
        }
    }
    if ($runtimeText -match 'ERROR:|status=fail' -or
        ($Presentation -eq 'OpenXRSimulator' -and
         $runtimeText -match 'fallback=desktop')) {
        throw 'Showcase runtime log contains an error, failure, or invalid fallback.'
    }
    if ($runtimeText -notmatch
        'NIKAMI_AURORA_ROOM_EMITTERS status=ready module=end_m01aa[^\r\n]*smoke=9 sparks=3 soft_fade=9 soft_fade_distance=0\.45') {
        throw 'Showcase runtime telemetry has incomplete Endar particle coverage.'
    }
    Assert-EnhancedKotorPbrCoverage -Evidence $runtimeText -ModuleId 'end_m01aa'

    for ($index = 0; $index -lt $resolvedGenericManifests.Count; $index++) {
        $module = $genericModules[$index]
        $genericIntermediate = Join-Path $temporaryDirectory "generic-$index-$module.ogv"
        $genericStdout = Join-Path $temporaryDirectory "generic-$index-$module-stdout.log"
        $genericStderr = Join-Path $temporaryDirectory "generic-$index-$module-stderr.log"
        $genericLaunch = @{
            Manifest = $resolvedGenericManifests[$index]
            GenericWorldShowcase = $true
            CleanCapture = $true
            MoviePath = $genericIntermediate
            MovieFps = $FramesPerSecond
            GodotStdoutPath = $genericStdout
            GodotStderrPath = $genericStderr
            TimeoutSeconds = 600
        }
        if (-not [string]::IsNullOrWhiteSpace($Godot)) {
            $genericLaunch.Godot = $Godot
        }
        & (Join-Path $PSScriptRoot 'Start-KotorGodot.ps1') @genericLaunch
        if ($LASTEXITCODE -ne 0) {
            throw "Generic KOTOR showcase recording failed for $module with exit code $LASTEXITCODE"
        }
        if (-not (Test-Path -LiteralPath $genericIntermediate -PathType Leaf) -or
            (Get-Item -LiteralPath $genericIntermediate).Length -lt 1MB) {
            throw "Godot Movie Maker did not produce a usable generic clip for $module."
        }
        $genericConsole = (Get-Content -LiteralPath $genericStdout -Raw) +
                          [Environment]::NewLine +
                          (Get-Content -LiteralPath $genericStderr -Raw)
        if ($genericConsole -match '(?m)^ERROR:|status=fail' -or
            $genericConsole.IndexOf(
                "NIKAMI_AURORA_KOTOR_BOOT status=pass module=$module mode=generic-world",
                [StringComparison]::Ordinal) -lt 0 -or
            $genericConsole.IndexOf(
                "NIKAMI_AURORA_GENERIC_SHOWCASE status=pass module=$module duration=8.000 camera=third-person motion=bounded-orbit+source-walkmesh renderer_scope=application",
                [StringComparison]::Ordinal) -lt 0 -or
            $genericConsole.IndexOf(
                'NIKAMI_AURORA_RENDER_QUALITY status=ready scope=application tier=enhanced backend=forward_plus',
                [StringComparison]::Ordinal) -lt 0) {
            throw "Generic KOTOR showcase evidence failed for $module."
        }
        Assert-EnhancedKotorPbrCoverage -Evidence $genericConsole -ModuleId $module
        $genericIntermediates += $genericIntermediate
    }

    $sourceClips = @($intermediate) + $genericIntermediates
    for ($index = 0; $index -lt $sourceClips.Count; $index++) {
        $normalized = Join-Path $temporaryDirectory "normalized-$index.mp4"
        $trimSeconds = if ($index -eq 0) { 0.0 } else { 1.25 }
        & $ffmpegCommand.Source -nostdin -hide_banner -loglevel error `
            -ss $trimSeconds -i $sourceClips[$index] `
            -map '0:v:0' -map '0:a:0' `
            -vf 'scale=1280:720:flags=lanczos,setsar=1' `
            -af 'aresample=48000' `
            -r $FramesPerSecond -c:v libx264 -preset slow -crf 18 `
            -pix_fmt yuv420p -c:a aac -b:a 192k $normalized
        if ($LASTEXITCODE -ne 0) {
            throw "ffmpeg clip normalization failed at index $index with exit code $LASTEXITCODE"
        }
        $normalizedClips += $normalized
    }
    $concatLines = $normalizedClips | ForEach-Object {
        "file '$($_.Replace("'", "''"))'"
    }
    [IO.File]::WriteAllLines($concatList, $concatLines)
    & $ffmpegCommand.Source -nostdin -hide_banner -loglevel error `
        -f concat -safe 0 -i $concatList -c copy -movflags '+faststart' $encoded
    if ($LASTEXITCODE -ne 0) {
        throw "ffmpeg multi-area concatenation failed with exit code $LASTEXITCODE"
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
        presentation = $Presentation
        modules = @('end_m01aa') + $genericModules
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
