[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RuntimeCatalog,
    [int]$ExpectedReadyProfiles = 352,
    [int]$ExpectedExactAtmosphereProfiles = 349,
    [int]$ExpectedBaseLightingProfiles = 3,
    [int]$ExpectedNavigationReadyProfiles = 343,
    [int]$ExpectedNavigationAbsentProfiles = 8,
    [int]$ExpectedNavigationUnsupportedProfiles = 1,
    [long]$ExpectedGlbs = 47561,
    [long]$ExpectedPbrMaterials = 81278,
    [int]$ExpectedEffectProfiles = 316,
    [int]$ExpectedEffectDefinitions = 69,
    [long]$ExpectedEffectInstances = 16229,
    [int]$ExpectedSupportedEffectDefinitions = 47,
    [long]$ExpectedSupportedEffectInstances = 13465,
    [int]$ExpectedFullySupportedEffectDefinitions = 24,
    [long]$ExpectedFullySupportedEffectInstances = 836,
    [int]$ExpectedPartialEffectDefinitions = 23,
    [long]$ExpectedPartialEffectInstances = 12629,
    [int]$ExpectedUnsupportedEffectDefinitions = 22,
    [long]$ExpectedUnsupportedEffectInstances = 2764,
    [int]$ExpectedUnsupportedEffectProfiles = 151,
    [int]$ExpectedPartialEffectProfiles = 278,
    [long]$ExpectedRenderedEffectEmitterPlacements = 42535,
    [long]$ExpectedIndependentScaleEmitterPlacements = 4103,
    [long]$ExpectedKnownSourceEffectEmitterPlacements = 69176,
    [long]$ExpectedKnownUnsupportedEffectEmitterPlacements = 26641,
    [long]$ExpectedUnknownEffectEmitterInventoryPlacements = 244,
    [long]$ExpectedUnsupportedDistortionEmitters = 7552,
    [long]$ExpectedUnsupportedSemanticEmitters = 9389,
    [string]$EffectAuditCli = '',
    [string]$AuditOutput = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Read-JsonFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "DAO fidelity input is absent: $Path"
    }
    Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Read-GlbJson([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 20 -or
        [BitConverter]::ToUInt32($bytes, 0) -ne 0x46546c67 -or
        [BitConverter]::ToUInt32($bytes, 4) -ne 2 -or
        [BitConverter]::ToUInt32($bytes, 8) -ne $bytes.Length) {
        throw "Malformed imported DAO GLB: $Path"
    }
    $jsonLength = [BitConverter]::ToUInt32($bytes, 12)
    $jsonType = [BitConverter]::ToUInt32($bytes, 16)
    if ($jsonType -ne 0x4e4f534a -or $jsonLength -gt $bytes.Length - 20) {
        throw "Imported DAO GLB has no valid JSON chunk: $Path"
    }
    $json = [Text.Encoding]::UTF8.GetString($bytes, 20, [int]$jsonLength).TrimEnd([char]0, ' ')
    $json | ConvertFrom-Json
}

function Test-FiniteNumber($Value) {
    if ($null -eq $Value) { return $false }
    $number = [double]$Value
    return -not [double]::IsNaN($number) -and -not [double]::IsInfinity($number)
}

function Get-PropertyValue($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Add-Count([hashtable]$Counts, [string]$Key, [long]$Value) {
    if ([string]::IsNullOrWhiteSpace($Key)) { $Key = 'unspecified' }
    if ($Counts.ContainsKey($Key)) { $Counts[$Key] = [long]$Counts[$Key] + $Value }
    else { $Counts[$Key] = $Value }
}

function Convert-CountsToRows([hashtable]$Counts) {
    @($Counts.GetEnumerator() |
        Sort-Object -Property @{ Expression = { [long]$_.Value }; Descending = $true }, Name |
        ForEach-Object {
            [pscustomobject]@{ reason = [string]$_.Key; placements = [long]$_.Value }
        })
}

function Assert-Vector($Value, [int]$Length, [string]$Label) {
    $channels = @($Value)
    if ($channels.Count -ne $Length -or
        @($channels | Where-Object { -not (Test-FiniteNumber $_) }).Count -ne 0) {
        throw "$Label must contain $Length finite values."
    }
}

$catalogPath = [IO.Path]::GetFullPath($RuntimeCatalog)
$catalog = Read-JsonFile $catalogPath
$readyAreas = @($catalog.areas | Where-Object { $_.ready -eq $true })
if ($readyAreas.Count -ne $ExpectedReadyProfiles) {
    throw "DAO ready-profile count drifted: $($readyAreas.Count)/$ExpectedReadyProfiles"
}

$effectDefinitions = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
$unsupportedEffectDefinitions = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
$visitedGlbs = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
$exactAtmosphereProfiles = 0
$baseLightingProfiles = 0
$profilesWithEffects = 0
$profilesWithUnsupportedEffects = 0
$profilesWithPartialEffects = 0
$effectInstances = 0L
$supportedEffectInstances = 0L
$unsupportedEffectInstances = 0L
$pbrMaterials = 0L
$pbrContractsReady = 0L
$alphaMaskMaterials = 0L
$alphaBlendMaterials = 0L
$doubleSidedMaterials = 0L
$levelRows = @{}
$gameRoots = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)

$exactEnvironmentFields = @(
    'sun_direction', 'sun_color', 'character_sun_color', 'fog_color',
    'fog_intensity', 'fog_cap', 'fog_zenith', 'fog_water_intensity',
    'fog_water_cap', 'distance_multiplier', 'atmosphere_alpha',
    'atmosphere_sun_color', 'sun_intensity', 'turbidity',
    'rayleigh_multiplier', 'mie_multiplier', 'phase_eccentricity',
    'cloud_density', 'cloud_sharpness', 'cloud_depth', 'cloud_range_1',
    'cloud_range_2', 'cloud_color', 'moon_scale', 'moon_alpha',
    'moon_rotation', 'skydome', 'probe_loaded', 'probe_matrix_r',
    'probe_matrix_g', 'probe_matrix_b')
$baseEnvironmentFields = @(
    'sun_direction', 'sun_color', 'character_sun_color', 'fog_color',
    'fog_intensity', 'sun_intensity', 'skydome', 'probe_loaded')

foreach ($area in $readyAreas) {
    $profile = Read-JsonFile ([IO.Path]::GetFullPath([string]$area.profilePath))
    [void]$gameRoots.Add([IO.Path]::GetFullPath([string]$profile.game_root))
    if ([string]::IsNullOrWhiteSpace([string]$profile.area) -or
        [string]::IsNullOrWhiteSpace([string]$profile.area_file)) {
        throw "DAO ready profile has no layout/manifest: $($area.id)"
    }
    $manifestPath = [IO.Path]::GetFullPath([string]$profile.area_file)
    $manifest = Read-JsonFile $manifestPath
    $environmentNames = @($manifest.environment.PSObject.Properties.Name)
    $atmosphereStatus = ''
    if (@($exactEnvironmentFields | Where-Object { $_ -notin $environmentNames }).Count -eq 0) {
        $exactAtmosphereProfiles++
        $atmosphereStatus = 'exact'
    }
    elseif ($environmentNames.Count -eq $baseEnvironmentFields.Count -and
            @($baseEnvironmentFields | Where-Object { $_ -notin $environmentNames }).Count -eq 0) {
        $baseLightingProfiles++
        $atmosphereStatus = 'base-only'
    }
    else {
        throw "DAO environment contract is neither exact nor base-only: $($profile.area)"
    }

    $areaHasEffects = $false
    foreach ($property in @($manifest.props.PSObject.Properties)) {
        $file = [string]$property.Value.file
        $resref = [IO.Path]::GetFileNameWithoutExtension($file)
        if ($resref -notmatch '^fxe_') { continue }
        $areaHasEffects = $true
        [void]$effectDefinitions.Add($resref)
        $instances = @($property.Value.instances).Count
        $effectInstances += $instances
    }
    if ($areaHasEffects) { $profilesWithEffects++ }

    $areaRoot = [IO.Path]::GetFullPath([string]$profile.area_root)
    $areaGlbs = 0
    $areaPbrMaterials = 0L
    $areaGlbMetrics = @{}
    foreach ($glb in Get-ChildItem -LiteralPath $areaRoot -Recurse -Filter '*.glb' -File) {
        $isNewGlb = $visitedGlbs.Add($glb.FullName)
        $areaGlbs++
        $document = Read-GlbJson $glb.FullName
        $glbPbrMaterials = 0
        foreach ($material in @((Get-PropertyValue $document 'materials'))) {
            if ($null -eq $material) { continue }
            $areaPbrMaterials++
            $glbPbrMaterials++
            if ($isNewGlb) { $pbrMaterials++ }
            $pbr = Get-PropertyValue $material 'pbrMetallicRoughness'
            $baseColorFactor = Get-PropertyValue $pbr 'baseColorFactor'
            if ($null -ne $baseColorFactor) {
                Assert-Vector $baseColorFactor 4 "$($glb.Name) baseColorFactor"
            }
            foreach ($field in 'metallicFactor', 'roughnessFactor') {
                $value = Get-PropertyValue $pbr $field
                if ($null -ne $value -and -not (Test-FiniteNumber $value)) {
                    throw "$($glb.Name) $field is not finite."
                }
            }
            $sourceAlphaMode = Get-PropertyValue $material 'alphaMode'
            $alphaMode = if ($null -eq $sourceAlphaMode) { 'OPAQUE' } else { [string]$sourceAlphaMode }
            if ($alphaMode -notin @('OPAQUE', 'MASK', 'BLEND')) {
                throw "$($glb.Name) has unsupported alphaMode $alphaMode."
            }
            $alphaCutoff = Get-PropertyValue $material 'alphaCutoff'
            if ($null -ne $alphaCutoff -and -not (Test-FiniteNumber $alphaCutoff)) {
                throw "$($glb.Name) alphaCutoff is not finite."
            }
            if ($isNewGlb) {
                if ($alphaMode -eq 'MASK') { $alphaMaskMaterials++ }
                if ($alphaMode -eq 'BLEND') { $alphaBlendMaterials++ }
                if ((Get-PropertyValue $material 'doubleSided') -eq $true) {
                    $doubleSidedMaterials++
                }
                $pbrContractsReady++
            }
        }
        $areaGlbMetrics[$glb.FullName] = [pscustomobject]@{
            pbrMaterials = $glbPbrMaterials
            sha256 = (Get-FileHash -LiteralPath $glb.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }

    $actorPlacements = @()
    $activeActorOrdinal = 0
    foreach ($actor in @($manifest.actors)) {
        if ($null -eq $actor -or [bool]$actor.active -ne $true) { continue }
        $modelPath = [IO.Path]::GetFullPath((Join-Path $areaRoot ([string]$actor.model)))
        $modelMetrics = $areaGlbMetrics[$modelPath]
        $actorValidation = @($area.actorValidation.actors | Where-Object {
            [string]$_.actor -eq [string]$actor.template -and
            [IO.Path]::GetFullPath([string]$_.model) -eq $modelPath
        } | Select-Object -First 1)
        $prerequisiteStatus = if ($null -ne $modelMetrics -and
                                     [int]$modelMetrics.pbrMaterials -gt 0 -and
                                     $actorValidation.Count -eq 1 -and
                                     [string]$actorValidation[0].status -eq 'pass') {
            'ready'
        } else { 'unsupported' }
        $actorPlacements += [pscustomobject]@{
            placementOrdinal = $activeActorOrdinal
            actorIdentity = [string]$actor.template
            modelRelativePath = [string]$actor.model
            modelSha256 = if ($null -eq $modelMetrics) { $null } else { [string]$modelMetrics.sha256 }
            authoredPosition = @($actor.position)
            authoredRotation = @($actor.rotation)
            active = $true
            archetype = if ($actorValidation.Count -eq 1) {
                [string]$actorValidation[0].stats.archetype
            } else { 'unknown' }
            importStatus = if ($actorValidation.Count -eq 1) {
                [string]$actorValidation[0].status
            } else { 'missing-validation' }
            pbrStatus = if ($null -ne $modelMetrics -and [int]$modelMetrics.pbrMaterials -gt 0) {
                'strict-pbr-ready'
            } else { 'unsupported' }
            pbrMaterials = if ($null -eq $modelMetrics) { 0 } else { [int]$modelMetrics.pbrMaterials }
            prerequisiteStatus = $prerequisiteStatus
            environmentFrameStatus = 'unverified'
            creatureFrameStatus = 'unverified'
            capturePath = $null
            captureSha256 = $null
        }
        $activeActorOrdinal++
    }

    $levelRows[[string]$area.key] = [pscustomobject]@{
        sourceKey = [string]$area.key
        areaId = [string]$area.id
        layout = [string]$area.layout
        sourceEntrySha256 = [string]$area.ownershipValidation.sourceEntrySha256
        sourceArchiveSha256 = [string]$area.ownershipValidation.sourceArchiveSha256
        profileSha256 = (Get-FileHash -LiteralPath ([IO.Path]::GetFullPath(
            [string]$area.profilePath)) -Algorithm SHA256).Hash.ToLowerInvariant()
        worldManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        geometry = [pscustomobject]@{
            status = if ([bool]$area.validation.passed -and
                         [string]$area.ownershipValidation.status -eq 'pass' -and
                         $areaGlbs -gt 0) { 'ready' } else { 'unsupported' }
            definitions = [int]$area.validation.definitions
            instances = [int]$area.validation.instances
            glbs = $areaGlbs
            runtimeCollisionDefinitions = [int]$area.validation.runtimeCollisionDefinitions
            runtimeCollisionInstances = [int]$area.validation.runtimeCollisionInstances
            runtimeCollisionShapes = [int]$area.validation.runtimeCollisionShapes
        }
        strictPbr = [pscustomobject]@{
            status = 'pass'
            renderableMaterials = $areaPbrMaterials
            pbrMaterials = $areaPbrMaterials
        }
        lightingAtmosphere = [pscustomobject]@{
            status = $atmosphereStatus
            exactFieldCount = $environmentNames.Count
            authoredLights = @($manifest.lights).Count
        }
        navigation = $null
        effects = $null
        cameraSpawnVisibility = [pscustomobject]@{
            status = 'unverified'
            reason = 'no-fresh-runtime-camera-spawn-and-los-result'
            activeWaypoints = @($manifest.waypoints | Where-Object { [bool]$_.active }).Count
            runtimeCollisionShapes = [int]$area.validation.runtimeCollisionShapes
        }
        playabilityTransition = [pscustomobject]@{
            status = 'unverified'
            reason = 'no-fresh-runtime-transition-traversal-result'
            activeTriggers = @($manifest.triggers | Where-Object { [bool]$_.active }).Count
            activeWaypoints = @($manifest.waypoints | Where-Object { [bool]$_.active }).Count
        }
        creatureGallery = [pscustomobject]@{
            status = 'unverified'
            expected = $actorPlacements.Count
            rendered = $null
            missing = $null
            unsupported = @($actorPlacements | Where-Object prerequisiteStatus -ne 'ready').Count
            environmentFrameStatus = 'unverified'
            creatureFrameStatus = 'unverified'
            placements = $actorPlacements
        }
        freshEvidence = [pscustomobject]@{
            status = 'absent'
            path = $null
            sha256 = $null
            reason = 'no-fresh-uninterrupted-runtime-evidence-joined'
        }
        blockers = @()
    }
}

if ($gameRoots.Count -ne 1) {
    throw "DAO effect audit requires one installed source root; observed $($gameRoots.Count)."
}
$repository = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($EffectAuditCli)) {
    $EffectAuditCli = Join-Path $repository 'src/Nikami.Aurora.Cli/bin/Release/net8.0/Nikami.Aurora.Cli.dll'
}
if (-not (Test-Path -LiteralPath $EffectAuditCli -PathType Leaf)) {
    throw "DAO effect audit CLI is absent; build Release first: $EffectAuditCli"
}
$effectAuditRoot = @($gameRoots)[0]
$effectAuditJson = & dotnet $EffectAuditCli dao-effect-audit `
    --root $effectAuditRoot --effects ((@($effectDefinitions) | Sort-Object) -join ',')
if ($LASTEXITCODE -notin @(0, 2)) {
    throw "DAO effect source audit failed with exit code $LASTEXITCODE."
}
$effectAudit = $effectAuditJson | ConvertFrom-Json
if ([int]$effectAudit.definitions -ne $effectDefinitions.Count -or
    @($effectAudit.results).Count -ne $effectDefinitions.Count) {
    throw 'DAO effect source audit did not cover the catalog definition inventory.'
}
$navigationAuditJson = & dotnet $EffectAuditCli dao-navigation-audit `
    --root $effectAuditRoot --layouts ((@($readyAreas | ForEach-Object {
        [string]$_.layout
    }) | Sort-Object -Unique) -join ',')
if ($LASTEXITCODE -notin @(0, 2)) {
    throw "DAO navigation source audit failed with exit code $LASTEXITCODE."
}
$navigationAudit = $navigationAuditJson | ConvertFrom-Json
$navigationContracts = @{}
foreach ($result in @($navigationAudit.results)) {
    $layout = [string]$result.layout
    if ($navigationContracts.ContainsKey($layout)) {
        throw "DAO navigation source audit returned a duplicate layout: $layout"
    }
    $navigationContracts[$layout] = $result
}
foreach ($area in $readyAreas) {
    $row = $levelRows[[string]$area.key]
    $navigation = $navigationContracts[[string]$area.layout]
    if ($null -eq $navigation) {
        throw "DAO navigation source audit omitted layout: $($area.layout)"
    }
    $row.navigation = [pscustomobject]@{
        status = [string]$navigation.status
        reason = [string]$navigation.reason
        sourceRelativePath = [string]$navigation.sourceRelativePath
        payloadSha256 = if ([string]::IsNullOrWhiteSpace(
            [string]$navigation.payloadSha256)) { $null } else {
            [string]$navigation.payloadSha256
        }
        columns = if ($null -eq $navigation.contract) { $null } else {
            [int]$navigation.contract.columns
        }
        rows = if ($null -eq $navigation.contract) { $null } else {
            [int]$navigation.contract.rows
        }
        cellSize = if ($null -eq $navigation.contract) { $null } else {
            [double]$navigation.contract.cellSize
        }
        walkableCells = if ($null -eq $navigation.contract) { $null } else {
            [int]$navigation.contract.walkableCells
        }
        prerequisiteOnly = $true
    }
}
$effectContracts = @{}
$supportedEffects = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
$partialEffects = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
$fullySupportedEffectInstances = 0L
$partialEffectInstances = 0L
$unsupportedDistortionEmitters = 0L
$unsupportedSemanticEmitters = 0L
$renderedEffectEmitterPlacements = 0L
$independentScaleEmitterPlacements = 0L
$knownSourceEffectEmitterPlacements = 0L
$unknownEffectEmitterInventoryPlacements = 0L
$readabilityValidatedEmitterPlacements = 0L
$maximumEffectCardDimension = 0.0
$maximumEffectVisibilityExtent = 0.0
$maximumEffectAtlasFrames = 0
$maximumEffectAnimationCycles = 0.0
$unsupportedDefinitionPlacementsByReason = @{}
$semanticEmitterPlacementsByReason = @{}
foreach ($result in @($effectAudit.results)) {
    $resref = [string]$result.resRef
    if (-not $effectDefinitions.Contains($resref) -or $effectContracts.ContainsKey($resref)) {
        throw "DAO effect source audit returned an unknown or duplicate definition: $resref"
    }
    $effectContracts[$resref] = $result
    if (@($result.unsupportedEmitterReasons).Count -ne
        [int]$result.unsupportedSemanticEmitters) {
        throw "DAO effect semantic-reason inventory is incomplete: $resref"
    }
    if ([bool]$result.supported) {
        $readability = @($result.emitterReadability)
        if ($readability.Count -ne [int]$result.emitters) {
            throw "DAO effect readability inventory is incomplete: $resref"
        }
        foreach ($contract in $readability) {
            $cardWidth = [double]$contract.maximumCardWidthMeters
            $cardHeight = [double]$contract.maximumCardHeightMeters
            $visibility = [double]$contract.visibilityBoundsExtentMeters
            $frames = [int]$contract.atlasFrames
            $cycles = [double]$contract.animationCyclesPerLifetime
            if (-not (Test-FiniteNumber $cardWidth) -or
                -not (Test-FiniteNumber $cardHeight) -or
                -not (Test-FiniteNumber $visibility) -or
                -not (Test-FiniteNumber $cycles) -or
                $cardWidth -le 0 -or $cardWidth -gt 128 -or
                $cardHeight -le 0 -or $cardHeight -gt 128 -or
                $visibility -lt 2 -or $visibility -gt 16384 -or
                $frames -le 0 -or $frames -gt 4096 -or
                $frames -ne [int]$contract.atlasColumns * [int]$contract.atlasRows -or
                [int]$contract.atlasCellWidth -le 0 -or
                [int]$contract.atlasCellHeight -le 0 -or
                $cycles -lt 0 -or $cycles -gt 4096) {
                throw "DAO effect readability bounds are invalid: $resref"
            }
            $fade = Get-PropertyValue $contract 'proximityFadeDistanceMeters'
            if ([bool]$contract.independentScaleAxes) {
                if ($null -ne $fade) {
                    throw "DAO independent-axis emitter acquired unsupported proximity fade: $resref"
                }
            }
            elseif ($null -eq $fade -or [double]$fade -lt 0.05 -or [double]$fade -gt 1.5) {
                throw "DAO standard emitter proximity fade is outside bounds: $resref"
            }
            $maximumEffectCardDimension = [Math]::Max(
                $maximumEffectCardDimension, [Math]::Max($cardWidth, $cardHeight))
            $maximumEffectVisibilityExtent = [Math]::Max(
                $maximumEffectVisibilityExtent, $visibility)
            $maximumEffectAtlasFrames = [Math]::Max($maximumEffectAtlasFrames, $frames)
            $maximumEffectAnimationCycles = [Math]::Max(
                $maximumEffectAnimationCycles, $cycles)
        }
        [void]$supportedEffects.Add($resref)
        if ([int]$result.unsupportedDistortionEmitters -gt 0 -or
            [int]$result.unsupportedSemanticEmitters -gt 0) {
            [void]$partialEffects.Add($resref)
        }
    }
    else {
        [void]$unsupportedEffectDefinitions.Add($resref)
    }
}

$supportedEffectInstances = 0L
$unsupportedEffectInstances = 0L
$profilesWithUnsupportedEffects = 0
$profilesWithPartialEffects = 0
foreach ($area in $readyAreas) {
    $profile = Read-JsonFile ([IO.Path]::GetFullPath([string]$area.profilePath))
    $manifest = Read-JsonFile ([IO.Path]::GetFullPath([string]$profile.area_file))
    $row = $levelRows[[string]$area.key]
    $areaHasUnsupportedEffects = $false
    $areaHasPartialEffects = $false
    $areaEffectDefinitions = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $areaUnsupportedDefinitions = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $areaPartialDefinitions = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $areaEffectInstances = 0L
    $areaRenderedEmitters = 0L
    $areaKnownSourceEmitters = 0L
    $areaUnknownEmitterInventory = 0L
    $areaDistortionEmitters = 0L
    $areaSemanticEmitters = 0L
    foreach ($property in @($manifest.props.PSObject.Properties)) {
        $resref = [IO.Path]::GetFileNameWithoutExtension([string]$property.Value.file)
        if ($resref -notmatch '^fxe_') { continue }
        [void]$areaEffectDefinitions.Add($resref)
        $instances = @($property.Value.instances).Count
        $areaEffectInstances += $instances
        $contract = $effectContracts[$resref]
        $sourceEmitters = Get-PropertyValue $contract 'sourceEmitters'
        if ($null -eq $sourceEmitters) {
            $unknownEffectEmitterInventoryPlacements += $instances
            $areaUnknownEmitterInventory += $instances
        }
        else {
            if (@($contract.emitterSemanticEvidence).Count -ne [int]$sourceEmitters) {
                throw "DAO source emitter semantic evidence is incomplete: $resref"
            }
            $knownSourceEffectEmitterPlacements += [long]$sourceEmitters * $instances
            $areaKnownSourceEmitters += [long]$sourceEmitters * $instances
        }
        if ([bool]$contract.supported) {
            if ($null -eq $sourceEmitters -or
                [long]$sourceEmitters -ne [long]$contract.emitters +
                    [long]$contract.unsupportedDistortionEmitters +
                    [long]$contract.unsupportedSemanticEmitters) {
                throw "DAO supported effect emitter inventory is incomplete: $resref"
            }
            $supportedEffectInstances += $instances
            $renderedEffectEmitterPlacements += [long]$contract.emitters * $instances
            $areaRenderedEmitters += [long]$contract.emitters * $instances
            $readabilityValidatedEmitterPlacements +=
                [long]@($contract.emitterReadability).Count * $instances
            if ([long]$contract.independentScaleEmitters -lt 0 -or
                [long]$contract.independentScaleEmitters -gt [long]$contract.emitters) {
                throw "DAO independent scale emitter inventory is invalid: $resref"
            }
            $independentScaleEmitterPlacements +=
                [long]$contract.independentScaleEmitters * $instances
            $unsupportedDistortionEmitters +=
                [long]$contract.unsupportedDistortionEmitters * $instances
            $areaDistortionEmitters +=
                [long]$contract.unsupportedDistortionEmitters * $instances
            $unsupportedSemanticEmitters +=
                [long]$contract.unsupportedSemanticEmitters * $instances
            $areaSemanticEmitters +=
                [long]$contract.unsupportedSemanticEmitters * $instances
            foreach ($reason in @($contract.unsupportedEmitterReasons)) {
                Add-Count $semanticEmitterPlacementsByReason ([string]$reason) $instances
            }
            if ($partialEffects.Contains($resref)) {
                $partialEffectInstances += $instances
                $areaHasPartialEffects = $true
                [void]$areaPartialDefinitions.Add($resref)
            }
            else {
                $fullySupportedEffectInstances += $instances
            }
        }
        else {
            $unsupportedEffectInstances += $instances
            $areaHasUnsupportedEffects = $true
            [void]$areaUnsupportedDefinitions.Add($resref)
            Add-Count $unsupportedDefinitionPlacementsByReason ([string]$contract.reason) $instances
        }
    }
    if ($areaHasUnsupportedEffects) { $profilesWithUnsupportedEffects++ }
    if ($areaHasPartialEffects) { $profilesWithPartialEffects++ }
    $effectStatus = if ($areaEffectDefinitions.Count -eq 0) { 'none' }
        elseif ($areaUnsupportedDefinitions.Count -gt 0) { 'unsupported' }
        elseif ($areaPartialDefinitions.Count -gt 0) { 'partial' }
        else { 'pass' }
    $row.effects = [pscustomobject]@{
        status = $effectStatus
        definitions = $areaEffectDefinitions.Count
        instances = $areaEffectInstances
        unsupportedDefinitions = $areaUnsupportedDefinitions.Count
        partialDefinitions = $areaPartialDefinitions.Count
        knownSourceEmitterPlacements = $areaKnownSourceEmitters
        renderedEmitterPlacements = $areaRenderedEmitters
        readabilityValidatedEmitterPlacements = $areaRenderedEmitters
        knownUnsupportedEmitterPlacements = $areaKnownSourceEmitters - $areaRenderedEmitters
        unknownEmitterInventoryPlacements = $areaUnknownEmitterInventory
        distortionEmitterPlacementsSkipped = $areaDistortionEmitters
        semanticEmitterPlacementsSkipped = $areaSemanticEmitters
    }

    $blockers = @()
    if ([string]$row.geometry.status -ne 'ready') { $blockers += 'geometry-unsupported' }
    if ([string]$row.strictPbr.status -ne 'pass') { $blockers += 'strict-pbr-incomplete' }
    if ([string]$row.lightingAtmosphere.status -ne 'exact') {
        $blockers += 'atmosphere-base-only'
    }
    if ([string]$row.navigation.status -ne 'ready') {
        $blockers += 'navigation-prerequisite-' + [string]$row.navigation.status
    }
    if ($effectStatus -in @('partial', 'unsupported')) {
        $blockers += 'effects-' + $effectStatus
    }
    if ([string]$row.cameraSpawnVisibility.status -ne 'pass') {
        $blockers += 'camera-spawn-visibility-unverified'
    }
    if ([string]$row.playabilityTransition.status -ne 'pass') {
        $blockers += 'playability-transition-unverified'
    }
    if ([string]$row.creatureGallery.status -ne 'pass') {
        $blockers += 'creature-gallery-unverified'
    }
    if ([string]$row.freshEvidence.status -ne 'pass') {
        $blockers += 'fresh-evidence-absent'
    }
    $row.blockers = $blockers
}

$orderedLevelRows = @($levelRows.Values | Sort-Object sourceKey)
$navigationReadyProfiles = @($orderedLevelRows |
    Where-Object { $_.navigation.status -eq 'ready' }).Count
$navigationAbsentProfiles = @($orderedLevelRows |
    Where-Object { $_.navigation.status -eq 'absent' }).Count
$navigationUnsupportedProfiles = @($orderedLevelRows |
    Where-Object { $_.navigation.status -eq 'unsupported' }).Count
$activeCreaturePlacements = [long](@($orderedLevelRows | ForEach-Object {
    [int]$_.creatureGallery.expected
}) | Measure-Object -Sum).Sum
$blockerRows = @($orderedLevelRows | ForEach-Object { @($_.blockers) } |
    Group-Object | Sort-Object -Property @{ Expression = { $_.Count }; Descending = $true }, Name |
    ForEach-Object {
        [pscustomobject]@{ blocker = $_.Name; areas = $_.Count }
    })

if ($levelRows.Count -ne $readyAreas.Count -or
    $navigationReadyProfiles -ne $ExpectedNavigationReadyProfiles -or
    $navigationAbsentProfiles -ne $ExpectedNavigationAbsentProfiles -or
    $navigationUnsupportedProfiles -ne $ExpectedNavigationUnsupportedProfiles -or
    $exactAtmosphereProfiles + $baseLightingProfiles -ne $readyAreas.Count -or
    $pbrContractsReady -ne $pbrMaterials -or
    $effectInstances -ne $supportedEffectInstances + $unsupportedEffectInstances -or
    $readabilityValidatedEmitterPlacements -ne $renderedEffectEmitterPlacements) {
    throw 'DAO catalog render-fidelity totals are internally inconsistent.'
}

$actual = @{
    navigationReadyProfiles = $navigationReadyProfiles
    navigationAbsentProfiles = $navigationAbsentProfiles
    navigationUnsupportedProfiles = $navigationUnsupportedProfiles
    exactAtmosphereProfiles = $exactAtmosphereProfiles
    baseLightingProfiles = $baseLightingProfiles
    glbs = $visitedGlbs.Count
    pbrMaterials = $pbrMaterials
    effectProfiles = $profilesWithEffects
    effectDefinitions = $effectDefinitions.Count
    effectInstances = $effectInstances
    supportedEffectDefinitions = $supportedEffects.Count
    supportedEffectInstances = $supportedEffectInstances
    fullySupportedEffectDefinitions = $supportedEffects.Count - $partialEffects.Count
    fullySupportedEffectInstances = $fullySupportedEffectInstances
    partialEffectDefinitions = $partialEffects.Count
    partialEffectInstances = $partialEffectInstances
    unsupportedEffectDefinitions = $unsupportedEffectDefinitions.Count
    unsupportedEffectInstances = $unsupportedEffectInstances
    unsupportedEffectProfiles = $profilesWithUnsupportedEffects
    partialEffectProfiles = $profilesWithPartialEffects
    renderedEffectEmitterPlacements = $renderedEffectEmitterPlacements
    readabilityValidatedEmitterPlacements = $readabilityValidatedEmitterPlacements
    independentScaleEmitterPlacements = $independentScaleEmitterPlacements
    knownSourceEffectEmitterPlacements = $knownSourceEffectEmitterPlacements
    knownUnsupportedEffectEmitterPlacements =
        $knownSourceEffectEmitterPlacements - $renderedEffectEmitterPlacements
    unknownEffectEmitterInventoryPlacements = $unknownEffectEmitterInventoryPlacements
    unsupportedDistortionEmitters = $unsupportedDistortionEmitters
    unsupportedSemanticEmitters = $unsupportedSemanticEmitters
}
$expected = @{
    navigationReadyProfiles = $ExpectedNavigationReadyProfiles
    navigationAbsentProfiles = $ExpectedNavigationAbsentProfiles
    navigationUnsupportedProfiles = $ExpectedNavigationUnsupportedProfiles
    exactAtmosphereProfiles = $ExpectedExactAtmosphereProfiles
    baseLightingProfiles = $ExpectedBaseLightingProfiles
    glbs = $ExpectedGlbs
    pbrMaterials = $ExpectedPbrMaterials
    effectProfiles = $ExpectedEffectProfiles
    effectDefinitions = $ExpectedEffectDefinitions
    effectInstances = $ExpectedEffectInstances
    supportedEffectDefinitions = $ExpectedSupportedEffectDefinitions
    supportedEffectInstances = $ExpectedSupportedEffectInstances
    fullySupportedEffectDefinitions = $ExpectedFullySupportedEffectDefinitions
    fullySupportedEffectInstances = $ExpectedFullySupportedEffectInstances
    partialEffectDefinitions = $ExpectedPartialEffectDefinitions
    partialEffectInstances = $ExpectedPartialEffectInstances
    unsupportedEffectDefinitions = $ExpectedUnsupportedEffectDefinitions
    unsupportedEffectInstances = $ExpectedUnsupportedEffectInstances
    unsupportedEffectProfiles = $ExpectedUnsupportedEffectProfiles
    partialEffectProfiles = $ExpectedPartialEffectProfiles
    renderedEffectEmitterPlacements = $ExpectedRenderedEffectEmitterPlacements
    readabilityValidatedEmitterPlacements = $ExpectedRenderedEffectEmitterPlacements
    independentScaleEmitterPlacements = $ExpectedIndependentScaleEmitterPlacements
    knownSourceEffectEmitterPlacements = $ExpectedKnownSourceEffectEmitterPlacements
    knownUnsupportedEffectEmitterPlacements =
        $ExpectedKnownUnsupportedEffectEmitterPlacements
    unknownEffectEmitterInventoryPlacements =
        $ExpectedUnknownEffectEmitterInventoryPlacements
    unsupportedDistortionEmitters = $ExpectedUnsupportedDistortionEmitters
    unsupportedSemanticEmitters = $ExpectedUnsupportedSemanticEmitters
}
foreach ($key in $expected.Keys) {
    if ([long]$actual[$key] -ne [long]$expected[$key]) {
        throw "DAO catalog $key drifted: $($actual[$key])/$($expected[$key])"
    }
}

$coverage = if ($effectInstances -eq 0) { 1.0 } else {
    [double]$supportedEffectInstances / [double]$effectInstances
}
$emitterCoverage = if ($knownSourceEffectEmitterPlacements -eq 0) { 1.0 } else {
    [double]$renderedEffectEmitterPlacements /
        [double]$knownSourceEffectEmitterPlacements
}
$status = if ($unsupportedEffectInstances -eq 0) { 'ready' } else { 'partial' }
$marker = "OPENDAO_CATALOG_RENDER_FIDELITY status=$status " +
    "profiles=$($readyAreas.Count) layout_neutral_policy=$($readyAreas.Count) " +
    "exact_atmo=$exactAtmosphereProfiles base_lighting_only=$baseLightingProfiles " +
    "navigation_ready=$navigationReadyProfiles navigation_absent=$navigationAbsentProfiles " +
    "navigation_unsupported=$navigationUnsupportedProfiles " +
    "glbs=$($visitedGlbs.Count) pbr_materials=$pbrMaterials " +
    "pbr_contract_ready=$pbrContractsReady alpha_mask=$alphaMaskMaterials " +
    "alpha_blend=$alphaBlendMaterials double_sided=$doubleSidedMaterials " +
    "effect_profiles=$profilesWithEffects effect_definitions=$($effectDefinitions.Count) " +
    "effect_instances=$effectInstances supported_effect_definitions=$($supportedEffects.Count) " +
    "supported_effect_instances=$supportedEffectInstances " +
    "supported_effect_coverage=$($coverage.ToString('P2', [Globalization.CultureInfo]::InvariantCulture)) " +
    "fully_supported_effect_definitions=$($supportedEffects.Count - $partialEffects.Count) " +
    "fully_supported_effect_instances=$fullySupportedEffectInstances " +
    "partial_effect_definitions=$($partialEffects.Count) " +
    "partial_effect_instances=$partialEffectInstances " +
    "unsupported_effect_definitions=$($unsupportedEffectDefinitions.Count) " +
    "unsupported_effect_instances=$unsupportedEffectInstances " +
    "unsupported_effect_profiles=$profilesWithUnsupportedEffects " +
    "partial_effect_profiles=$profilesWithPartialEffects " +
    "rendered_effect_emitter_placements=$renderedEffectEmitterPlacements " +
    "readability_validated_emitter_placements=$readabilityValidatedEmitterPlacements " +
    "maximum_effect_card_dimension=$($maximumEffectCardDimension.ToString('R', [Globalization.CultureInfo]::InvariantCulture)) " +
    "maximum_effect_visibility_extent=$($maximumEffectVisibilityExtent.ToString('R', [Globalization.CultureInfo]::InvariantCulture)) " +
    "maximum_effect_atlas_frames=$maximumEffectAtlasFrames " +
    "maximum_effect_animation_cycles=$($maximumEffectAnimationCycles.ToString('R', [Globalization.CultureInfo]::InvariantCulture)) " +
    "independent_scale_emitter_placements=$independentScaleEmitterPlacements " +
    "known_source_effect_emitter_placements=$knownSourceEffectEmitterPlacements " +
    "known_unsupported_effect_emitter_placements=$($knownSourceEffectEmitterPlacements - $renderedEffectEmitterPlacements) " +
    "known_effect_emitter_coverage=$($emitterCoverage.ToString('P2', [Globalization.CultureInfo]::InvariantCulture)) " +
    "unknown_effect_emitter_inventory_placements=$unknownEffectEmitterInventoryPlacements " +
    "distortion_emitters_skipped=$unsupportedDistortionEmitters " +
    "semantic_emitters_skipped=$unsupportedSemanticEmitters " +
    "active_creature_placements=$activeCreaturePlacements " +
    "runtime_verified_areas=0 creature_gallery_verified_areas=0 " +
    "readability_policy=source-scale+atlas+timing+bounded-fade " +
    "effect_mao_semantics=decoded-subset mao_semantics=unsupported parity_claim=none"
$auditResult = [pscustomobject]@{
    schema = 'opendao-all-level-render-matrix-v1'
    generatedAtUtc = [DateTime]::UtcNow.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
    runtimeCatalogSha256 = (Get-FileHash -LiteralPath $catalogPath -Algorithm SHA256).Hash.ToLowerInvariant()
    status = $status
    readyProfiles = $readyAreas.Count
    runtimeVerifiedProfiles = 0
    creatureGalleryVerifiedProfiles = 0
    navigationReadyProfiles = $navigationReadyProfiles
    navigationAbsentProfiles = $navigationAbsentProfiles
    navigationUnsupportedProfiles = $navigationUnsupportedProfiles
    exactAtmosphereProfiles = $exactAtmosphereProfiles
    baseLightingOnlyProfiles = $baseLightingProfiles
    glbs = $visitedGlbs.Count
    pbrMaterials = $pbrMaterials
    effectProfiles = $profilesWithEffects
    effectDefinitions = $effectDefinitions.Count
    effectInstances = $effectInstances
    supportedEffectDefinitions = $supportedEffects.Count
    supportedEffectInstances = $supportedEffectInstances
    fullySupportedEffectDefinitions = $supportedEffects.Count - $partialEffects.Count
    fullySupportedEffectInstances = $fullySupportedEffectInstances
    partialEffectDefinitions = $partialEffects.Count
    partialEffectInstances = $partialEffectInstances
    unsupportedEffectDefinitions = $unsupportedEffectDefinitions.Count
    unsupportedEffectInstances = $unsupportedEffectInstances
    unsupportedEffectProfiles = $profilesWithUnsupportedEffects
    partialEffectProfiles = $profilesWithPartialEffects
    renderedEffectEmitterPlacements = $renderedEffectEmitterPlacements
    readabilityValidatedEmitterPlacements = $readabilityValidatedEmitterPlacements
    maximumEffectCardDimension = $maximumEffectCardDimension
    maximumEffectVisibilityExtent = $maximumEffectVisibilityExtent
    maximumEffectAtlasFrames = $maximumEffectAtlasFrames
    maximumEffectAnimationCycles = $maximumEffectAnimationCycles
    independentScaleEmitterPlacements = $independentScaleEmitterPlacements
    knownSourceEffectEmitterPlacements = $knownSourceEffectEmitterPlacements
    knownUnsupportedEffectEmitterPlacements =
        $knownSourceEffectEmitterPlacements - $renderedEffectEmitterPlacements
    knownEffectEmitterCoverage = $emitterCoverage
    unknownEffectEmitterInventoryPlacements = $unknownEffectEmitterInventoryPlacements
    distortionEmittersSkipped = $unsupportedDistortionEmitters
    semanticEmittersSkipped = $unsupportedSemanticEmitters
    activeCreaturePlacements = $activeCreaturePlacements
    unsupportedDefinitionPlacementsByReason =
        Convert-CountsToRows $unsupportedDefinitionPlacementsByReason
    unsupportedSemanticEmitterPlacementsByReason =
        Convert-CountsToRows $semanticEmitterPlacementsByReason
    blockerTable = $blockerRows
    levels = $orderedLevelRows
}
if (-not [string]::IsNullOrWhiteSpace($AuditOutput)) {
    $auditOutputPath = [IO.Path]::GetFullPath($AuditOutput)
    $auditOutputDirectory = [IO.Path]::GetDirectoryName($auditOutputPath)
    if ([string]::IsNullOrWhiteSpace($auditOutputDirectory)) {
        throw "DAO audit output has no parent directory: $auditOutputPath"
    }
    [void][IO.Directory]::CreateDirectory($auditOutputDirectory)
    [IO.File]::WriteAllText($auditOutputPath,
        ($auditResult | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
}
Write-Output $marker
Write-Output $auditResult
