[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RuntimeCatalog,
    [Parameter(Mandatory = $true)]
    [string]$GodotConsolePath,
    [Parameter(Mandatory = $true)]
    [string]$OutputRoot,
    [string]$CacheRoot = '',
    [string]$GeneratedRoot = '',
    [string]$GameRoot = '',
    [string[]]$Selectors = @(),
    [ValidateRange(0, 352)]
    [int]$MaximumAreas = 0,
    [ValidateRange(800, 7680)]
    [int]$ViewportWidth = 1280,
    [ValidateRange(600, 4320)]
    [int]$ViewportHeight = 720,
    [switch]$SkipAuthoredWalk,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Resolve-RequiredFile([string]$Path, [string]$Label) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "$Label is absent: $resolved"
    }
    $resolved
}

function Get-Token([string]$Row, [string]$Name) {
    $match = [regex]::Match($Row, "(?:^| )$([regex]::Escape($Name))=(?<value>[^ ]+)")
    if (-not $match.Success) { return $null }
    $match.Groups['value'].Value
}

function Select-LastRow([string]$Content, [string]$Marker) {
    @([regex]::Matches($Content, "(?m)^$([regex]::Escape($Marker))[^\r\n]*") |
        ForEach-Object Value | Select-Object -Last 1)[0]
}

$catalogPath = Resolve-RequiredFile $RuntimeCatalog 'DAO runtime catalog'
$godot = Resolve-RequiredFile $GodotConsolePath 'Godot console'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repository 'godot'
$catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
$areas = @($catalog.areas | Where-Object { [bool]$_.ready })
if ($Selectors.Count -gt 0) {
    $requested = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($selector in $Selectors) { [void]$requested.Add($selector.Trim()) }
    $areas = @($areas | Where-Object {
        $requested.Contains([string]$_.key) -or
        $requested.Contains([string]$_.id) -or
        $requested.Contains("$($_.id)/$($_.layout)")
    })
    $unresolved = @($Selectors | Where-Object {
        $selector = $_
        -not @($areas | Where-Object {
            [string]$_.key -eq $selector -or [string]$_.id -eq $selector -or
            "$($_.id)/$($_.layout)" -eq $selector
        }).Count
    })
    if ($unresolved.Count -gt 0) {
        throw "DAO area selector(s) did not resolve: $($unresolved -join ', ')"
    }
}
$areas = @($areas | Sort-Object key)
if ($MaximumAreas -gt 0) { $areas = @($areas | Select-Object -First $MaximumAreas) }
if ($areas.Count -eq 0) { throw 'No ready DAO areas were selected.' }

$output = [IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$logRoot = Join-Path $output 'logs'
$stateRoot = Join-Path $output 'state'
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
New-Item -ItemType Directory -Force -Path $stateRoot | Out-Null

if (-not $SkipBuild) {
    & dotnet build (Join-Path $project 'Nikami.Aurora.Godot.csproj') -c Debug
    if ($LASTEXITCODE -ne 0) { throw 'Nikami Aurora Godot build failed.' }
}

$environmentNames = @(
    'OPENDAO_PROFILE', 'OPENDAO_AREA_RUNTIME_EVIDENCE_ROOT', 'OPENDAO_CONTINUE',
    'OPENDAO_IGNORE_PENDING_TRANSITION', 'OPENDAO_TEST_NO_PERSIST',
    'OPENDAO_CHARACTER_PROFILE', 'OPENDAO_PLAYER_SESSION', 'OPENDAO_PENDING_TRANSITION',
    'DAOPEN_STORY_STATE', 'OPENDAO_CATALOG', 'NIKAMI_AURORA_PROFILE',
    'NIKAMI_AURORA_DAO_CACHE_ROOT', 'NIKAMI_AURORA_DAO_GENERATED_ROOT',
    'NIKAMI_AURORA_PRESENTATION_TIER', 'DRAGON_AGE_GODOT_GAME_ROOT',
    'OPENDAO_DISABLE_MODEL_DISK_CACHE', 'OPENDAO_AREA_RUNTIME_EVIDENCE_WALK'
)
$previous = @{}
foreach ($name in $environmentNames) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

$results = @()
try {
    foreach ($area in $areas) {
        $profilePath = Resolve-RequiredFile ([string]$area.profilePath) `
            "DAO profile $($area.key)"
        $slug = ([string]$area.id + '-' + [string]$area.layout + '-' +
            ([string]$area.ownershipValidation.sourceEntrySha256).Substring(0, 12)) `
            -replace '[^A-Za-z0-9_-]', '-'
        $log = Join-Path $logRoot "$slug.log"
        if (Test-Path -LiteralPath $log) {
            throw "Refusing to overwrite existing DAO runtime log: $log"
        }
        $settings = @{
            OPENDAO_PROFILE = $profilePath
            OPENDAO_AREA_RUNTIME_EVIDENCE_ROOT = $output
            OPENDAO_CONTINUE = '0'
            OPENDAO_IGNORE_PENDING_TRANSITION = '1'
            OPENDAO_TEST_NO_PERSIST = '1'
            OPENDAO_CHARACTER_PROFILE = Join-Path $stateRoot "$slug-character.json"
            OPENDAO_PLAYER_SESSION = Join-Path $stateRoot "$slug-session.json"
            OPENDAO_PENDING_TRANSITION = Join-Path $stateRoot "$slug-transition.json"
            DAOPEN_STORY_STATE = Join-Path $stateRoot "$slug-story.json"
            OPENDAO_CATALOG = $catalogPath
            NIKAMI_AURORA_PROFILE = 'dragon-age-origins'
            NIKAMI_AURORA_PRESENTATION_TIER = 'enhanced'
            OPENDAO_DISABLE_MODEL_DISK_CACHE = '1'
            OPENDAO_AREA_RUNTIME_EVIDENCE_WALK = if ($SkipAuthoredWalk) { '0' } else { '1' }
        }
        if ($CacheRoot.Length -gt 0) {
            $settings.NIKAMI_AURORA_DAO_CACHE_ROOT = [IO.Path]::GetFullPath($CacheRoot)
        }
        if ($GeneratedRoot.Length -gt 0) {
            $settings.NIKAMI_AURORA_DAO_GENERATED_ROOT = [IO.Path]::GetFullPath($GeneratedRoot)
        }
        if ($GameRoot.Length -gt 0) {
            $settings.DRAGON_AGE_GODOT_GAME_ROOT = [IO.Path]::GetFullPath($GameRoot)
        }
        foreach ($name in $environmentNames) {
            [Environment]::SetEnvironmentVariable($name, '', 'Process')
        }
        foreach ($entry in $settings.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }

        $savedPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            & $godot '--path' $project '--rendering-method' 'forward_plus' `
                '--resolution' "${ViewportWidth}x${ViewportHeight}" `
                '--log-file' $log 'res://dao_world.tscn' 2>&1 | Out-Null
            $exitCode = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $savedPreference }

        if (-not (Test-Path -LiteralPath $log -PathType Leaf)) {
            throw "DAO runtime did not write a log: $log"
        }
        $content = Get-Content -LiteralPath $log -Raw
        if ($exitCode -ne 0) {
            throw "DAO runtime failed for $($area.key) with exit code $exitCode. Log: $log"
        }
        if ($content -match '(?m)^ERROR:|OPENDAO_AREA_RUNTIME_EVIDENCE status=fail') {
            throw "DAO runtime logged a fatal error for $($area.key). Log: $log"
        }
        $quality = Select-LastRow $content 'NIKAMI_AURORA_RENDER_QUALITY'
        $materials = Select-LastRow $content 'OPENDAO_WORLD_MATERIAL_CENSUS'
        $playerPbr = Select-LastRow $content 'OPENDAO_CHARACTER_PBR_PIPELINE'
        $environment = Select-LastRow $content 'OPENDAO_AREA_ENVIRONMENT_FRAME'
        $gallery = Select-LastRow $content 'OPENDAO_IN_WORLD_CREATURE_GALLERY'
        $evidence = Select-LastRow $content 'OPENDAO_AREA_RUNTIME_EVIDENCE'
        foreach ($required in @($quality, $materials, $playerPbr, $environment, $gallery, $evidence)) {
            if ([string]::IsNullOrWhiteSpace($required)) {
                throw "DAO runtime omitted a required fidelity marker for $($area.key). Log: $log"
            }
        }
        $surfaces = [int](Get-Token $materials 'surfaces')
        if ((Get-Token $quality 'backend') -ne 'forward_plus' -or
            (Get-Token $quality 'tier') -ne 'enhanced' -or
            (Get-Token $materials 'binding_status') -ne 'ready' -or
            (Get-Token $materials 'identity_status') -ne 'ready' -or
            [int](Get-Token $materials 'bound') -ne $surfaces -or
            [int](Get-Token $materials 'missing') -ne 0 -or
            [int](Get-Token $materials 'payload_identity_verified') -ne $surfaces -or
            [int](Get-Token $materials 'unresolved_identity') -ne 0 -or
            [int](Get-Token $materials 'pbr_contract_ready') -ne $surfaces -or
            [int](Get-Token $playerPbr 'authored_unshaded') -ne 0 -or
            (Get-Token $environment 'status') -ne 'pass' -or
            (Get-Token $environment 'pbr') -ne 'strict-runtime-ready' -or
            [int](Get-Token $environment 'unshaded_fallback') -ne 0 -or
            (Get-Token $evidence 'status') -ne 'partial') {
            throw "DAO strict enhanced/PBR evidence failed for $($area.key). Log: $log"
        }
        $manifestPath = Get-Token $evidence 'manifest'
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            throw "DAO evidence manifest is absent for $($area.key): $manifestPath"
        }
        $manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($manifestSha256 -ne (Get-Token $evidence 'sha256')) {
            throw "DAO evidence manifest hash mismatch for $($area.key)."
        }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ([string]$manifest.schema -ne 'opendao-area-runtime-evidence-v1' -or
            [string]$manifest.sourceKey -ne [string]$area.key -or
            [string]$manifest.areaId -ne [string]$area.id -or
            [string]$manifest.layout -ne [string]$area.layout) {
            throw "DAO evidence identity mismatch for $($area.key)."
        }
        $results += [pscustomobject]@{
            sourceKey = [string]$area.key
            areaId = [string]$area.id
            layout = [string]$area.layout
            status = 'pass'
            galleryStatus = [string]$manifest.creatureGallery.status
            expectedCreatures = [int]$manifest.creatureGallery.expected
            renderedCreatures = [int]$manifest.creatureGallery.rendered
            manifestPath = $manifestPath
            manifestSha256 = $manifestSha256
            logPath = $log
            logSha256 = (Get-FileHash -LiteralPath $log -Algorithm SHA256).Hash.ToLowerInvariant()
            aestheticStatus = 'manual-review-required'
        }
        Write-Output ("OPENDAO_AREA_CAPTURE_RESULT status=pass " +
            "area=$($area.id) layout=$($area.layout) " +
            "gallery_status=$([string]$manifest.creatureGallery.status) " +
            "creatures=$([int]$manifest.creatureGallery.rendered)/$([int]$manifest.creatureGallery.expected) " +
            "manifest=$manifestPath aesthetic_status=manual-review-required")
    }
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
    }
}

$summaryPath = Join-Path $output 'opendao-area-runtime-evidence-index-v1.json'
if (Test-Path -LiteralPath $summaryPath) {
    throw "Refusing to overwrite existing DAO evidence index: $summaryPath"
}
$galleryPass = @($results | Where-Object galleryStatus -eq 'pass').Count
$summary = [ordered]@{
    schema = 'opendao-area-runtime-evidence-index-v1'
    status = if ($galleryPass -eq $results.Count) { 'pass' } else { 'partial' }
    requested = $areas.Count
    environmentPassed = $results.Count
    creatureGalleryPassed = $galleryPass
    creatureGalleryPartialOrUnverified = $results.Count - $galleryPass
    aestheticStatus = 'manual-review-required'
    results = $results
}
$summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $summaryPath -Encoding utf8
$summaryHash = (Get-FileHash -LiteralPath $summaryPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Output ("OPENDAO_ALL_AREA_RUNTIME_EVIDENCE status=$($summary.status) " +
    "requested=$($areas.Count) environment_pass=$($results.Count) " +
    "creature_gallery_pass=$galleryPass " +
    "creature_gallery_unverified=$($results.Count - $galleryPass) " +
    "index=$summaryPath sha256=$summaryHash aesthetic_status=manual-review-required")
