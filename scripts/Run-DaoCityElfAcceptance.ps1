[CmdletBinding()]
param(
    [string]$GodotConsolePath = '',
    [string]$GameRoot = '',
    [string]$CacheRoot = '',
    [string]$GeneratedRoot = '',
    [string]$OutputRoot = '',
    [string]$Origin = 'city-elf',
    [ValidateSet('male', 'female')]
    [string]$Gender = 'female',
    [string]$CharacterName = 'Kallian',
    [ValidateSet('', 'warrior', 'rogue', 'mage')]
    [string]$CharacterClass = '',
    [ValidateSet('preset-1', 'preset-2', 'preset-3', 'preset-4')]
    [string]$CharacterAppearance = 'preset-4',
    [double]$CutsceneTimeScale = 1,
    [ValidateRange(800, 7680)]
    [int]$ViewportWidth = 1280,
    [ValidateRange(600, 4320)]
    [int]$ViewportHeight = 720,
    [string]$OutputVideo = '',
    [ValidateRange(24, 60)]
    [int]$FramesPerSecond = 30,
    [ValidateRange(1, 600)]
    [double]$MinimumVideoSeconds = 20,
    [ValidateRange(1, 600)]
    [double]$MaximumVideoSeconds = 180,
    [ValidateRange(0, 30)]
    [double]$MenuHoldSeconds = 3,
    [ValidateRange(0, 30)]
    [double]$CharacterHoldSeconds = 3,
    [ValidateRange(0, 30)]
    [double]$GameplayHoldSeconds = 0,
    [ValidateRange(0, 600)]
    [int]$PlayableStartupFrames = 12,
    [ValidateRange(0, 600)]
    [int]$AlienageWarmupFrames = 24,
    [string]$DialogueChoices = '',
    [string]$DialogueChoiceHoldSeconds = '',
    [switch]$RunLocomotionSmoke,
    [switch]$RunPlayableSmoke,
    [switch]$SourcePresentation,
    [switch]$Headless,
    [switch]$SkipBuild,
    [switch]$SkipCaptures
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($GodotConsolePath)) {
    $godotCommand = Get-Command 'Godot_v4.7.1-stable_mono_win64_console.exe' `
        -ErrorAction SilentlyContinue
    if (-not $godotCommand) {
        throw 'Godot 4.7.1 .NET console was not found. Pass -GodotConsolePath.'
    }
    $GodotConsolePath = $godotCommand.Source
}
$GodotConsolePath = [IO.Path]::GetFullPath($GodotConsolePath)
if (-not (Test-Path -LiteralPath $GodotConsolePath -PathType Leaf)) {
    throw "Godot console was not found: $GodotConsolePath"
}
if ($Origin -ne 'city-elf') {
    throw "The current Aurora DAO acceptance route is city-elf; received $Origin."
}
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repository 'godot'
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repository 'artifacts/runtime-acceptance/full-flow'
}
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$OutputRoot = (Resolve-Path -LiteralPath $OutputRoot).Path
$requestedVideoPath = ''
$videoValidated = $false
$nativeMovie = ''
if (-not [string]::IsNullOrWhiteSpace($OutputVideo)) {
    if ($MaximumVideoSeconds -le $MinimumVideoSeconds) {
        throw '-MaximumVideoSeconds must be greater than -MinimumVideoSeconds.'
    }
    $videoParent = Split-Path -Parent $OutputVideo
    if ([string]::IsNullOrWhiteSpace($videoParent)) { $videoParent = (Get-Location).Path }
    New-Item -ItemType Directory -Force -Path $videoParent | Out-Null
    $requestedVideoPath = Join-Path (Resolve-Path -LiteralPath $videoParent).Path `
        (Split-Path -Leaf $OutputVideo)
    if (Test-Path -LiteralPath $requestedVideoPath) {
        throw "Refusing to overwrite an existing proof video: $requestedVideoPath"
    }
}
$route = @{ area = 'bec110ar_players_house'; waypoint = 'bec110wp_start'; cutscene = 'start_wake' }
$characterMorphs = @{
    'female:preset-1' = @{
        resource = 'ef_cps_p01.mop'
        sha256 = '3824bd11fd6ba7820be055b1a8b296a9faa9ee83f5b3a56d51bc9bfcfbb71a62'
    }
    'female:preset-2' = @{
        resource = 'ef_cps_p02.mop'
        sha256 = '4e5b5543cdccc4a03f9a34337790a8f608825f19a2b0d8f271bef2d855f3faec'
    }
    'female:preset-3' = @{
        resource = 'ef_cps_p03.mop'
        sha256 = '0ea48e526b5d7edc99318fd00aad7d3ad868d5f19d3e3cd1d112323ddc91ca65'
    }
    'female:preset-4' = @{
        resource = 'ef_cps_p04.mop'
        sha256 = '1414d567f130fb14c44776f92aa8d154cbdf2ba59b13f250be69f24cb9292fdf'
    }
    'male:preset-1' = @{
        resource = 'em_cps_p01.mop'
        sha256 = '5ee71ea686ab9a416007e6c76383d6ab0890d4e0c935febea400e15efa6a18c8'
    }
    'male:preset-2' = @{
        resource = 'em_cps_p02.mop'
        sha256 = '07c5a851294651ac49a27c8b075622c93e8ee42be64f3c275c26f7de2928229c'
    }
    'male:preset-3' = @{
        resource = 'em_cps_p03.mop'
        sha256 = 'aade2e0bcc4584fc2fe8d95ac8a6c127a8a629916e4578893a095a8251206092'
    }
    'male:preset-4' = @{
        resource = 'em_cps_p04.mop'
        sha256 = '11446fc707f17e07b5f85b175cca302a2910f2335151c1c5b9707004cd454b77'
    }
}
$characterMorph = $characterMorphs["${Gender}:${CharacterAppearance}"]
if ($null -eq $characterMorph) {
    throw "No source-bound city-elf morph identity exists for ${Gender}:${CharacterAppearance}."
}
$characterStem = [IO.Path]::GetFileNameWithoutExtension($characterMorph.resource)

if ([string]::IsNullOrWhiteSpace($CacheRoot) -or
    [string]::IsNullOrWhiteSpace($GeneratedRoot)) {
    throw '-CacheRoot and -GeneratedRoot are required until Aurora owns fresh DAO import.'
}
$CacheRoot = [IO.Path]::GetFullPath($CacheRoot)
$GeneratedRoot = [IO.Path]::GetFullPath($GeneratedRoot)
$requiredImportedFiles = @(
    (Join-Path $CacheRoot 'runtime-catalog.json'),
    (Join-Path $CacheRoot 'dao-gda.json'),
    (Join-Path $GeneratedRoot 'cutscenes\start_wake\media-manifest.json'),
    (Join-Path $GeneratedRoot 'dialogues\bec110cr_shianni\dialogue-manifest.json')
)
foreach ($requiredImportedFile in $requiredImportedFiles) {
    if (-not (Test-Path -LiteralPath $requiredImportedFile -PathType Leaf)) {
        throw "Aurora DAO imported source is incomplete: $requiredImportedFile"
    }
}

$variables = @(
    'OPENDAO_CHARACTER_CREATION_ACCEPTANCE',
    'OPENDAO_ACCEPTANCE_ORIGIN',
    'OPENDAO_ACCEPTANCE_GENDER',
    'OPENDAO_ACCEPTANCE_NAME',
    'OPENDAO_ACCEPTANCE_CLASS',
    'OPENDAO_ACCEPTANCE_APPEARANCE',
    'OPENDAO_FLOW_LOCOMOTION',
    'OPENDAO_CITY_ELF_PLAYABLE_SMOKE',
    'OPENDAO_CITY_ELF_PLAYABLE_SMOKE_STAGE',
    'OPENDAO_PLAYABLE_DESTINATION_CAPTURE',
    'OPENDAO_CITY_ELF_SKY_CAPTURE',
    'OPENDAO_CUTSCENE_TIME_SCALE',
    'OPENDAO_TEST_NO_PERSIST',
    'OPENDAO_MAIN_MENU_CAPTURE',
    'OPENDAO_LOADING_CAPTURE',
    'OPENDAO_LOADING_CAPTURE_EXIT',
    'OPENDAO_LOADING_ADVANCE_FRAMES',
    'OPENDAO_CHARACTER_DEFAULT_CAPTURE',
    'OPENDAO_CHARACTER_CREATION_CAPTURE',
    'OPENDAO_CHARACTER_APPEARANCE_CAPTURE',
    'OPENDAO_CUTSCENE_CAPTURE',
    'OPENDAO_FACEFX_CAPTURE',
    'OPENDAO_DIALOGUE_CAPTURE',
    'OPENDAO_DIALOGUE_LINE_CAPTURE',
    'OPENDAO_DIALOGUE_CHOICES',
    'OPENDAO_DIALOGUE_CHOICE_HOLD_SECONDS',
    'OPENDAO_POSE_TRANSITION_CAPTURE',
    'OPENDAO_STANDING_DIALOGUE_CAPTURE',
    'OPENDAO_GAME_START_CAPTURE',
    'OPENDAO_LOCOMOTION_CAPTURE',
    'OPENDAO_SELECTED_PROFILE',
    'OPENDAO_PLAYER_SESSION',
    'OPENDAO_CHARACTER_PROFILE',
    'OPENDAO_PENDING_TRANSITION',
    'DAOPEN_STORY_STATE',
    'OPENDAO_CONTINUE'
    'DRAGON_AGE_GODOT_GAME_ROOT'
    'OPENDAO_CATALOG'
    'NIKAMI_AURORA_PROFILE'
    'NIKAMI_AURORA_DAO_CACHE_ROOT'
    'NIKAMI_AURORA_DAO_GENERATED_ROOT'
    'NIKAMI_AURORA_PRESENTATION_TIER'
    'OPENDAO_ACCEPTANCE_MENU_HOLD_FRAMES'
    'OPENDAO_ACCEPTANCE_CHARACTER_HOLD_FRAMES'
    'OPENDAO_ACCEPTANCE_GAMEPLAY_HOLD_FRAMES'
    'OPENDAO_ACCEPTANCE_PLAYABLE_STARTUP_FRAMES'
    'OPENDAO_ACCEPTANCE_ALIENAGE_WARMUP_FRAMES'
)
$previous = @{}
foreach ($name in $variables) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    foreach ($name in $variables) {
        [Environment]::SetEnvironmentVariable($name, '', 'Process')
    }
    $stateRoot = Join-Path $OutputRoot 'state'
    New-Item -ItemType Directory -Force -Path $stateRoot | Out-Null
    $settings = @{
        OPENDAO_CHARACTER_CREATION_ACCEPTANCE = '1'
        OPENDAO_ACCEPTANCE_ORIGIN = $Origin
        OPENDAO_ACCEPTANCE_GENDER = $Gender
        OPENDAO_ACCEPTANCE_NAME = $CharacterName
        OPENDAO_ACCEPTANCE_CLASS = $CharacterClass
        OPENDAO_ACCEPTANCE_APPEARANCE = $CharacterAppearance
        OPENDAO_CUTSCENE_TIME_SCALE = [string]$CutsceneTimeScale
        OPENDAO_TEST_NO_PERSIST = '1'
        OPENDAO_SELECTED_PROFILE = Join-Path $stateRoot 'selected-profile.json'
        OPENDAO_PLAYER_SESSION = Join-Path $stateRoot 'player-session.json'
        OPENDAO_CHARACTER_PROFILE = Join-Path $stateRoot 'character.json'
        OPENDAO_PENDING_TRANSITION = Join-Path $stateRoot 'pending-transition.json'
        DAOPEN_STORY_STATE = Join-Path $stateRoot 'story-state.json'
        OPENDAO_CATALOG = Join-Path $CacheRoot 'runtime-catalog.json'
        NIKAMI_AURORA_PROFILE = 'dragon-age-origins'
        NIKAMI_AURORA_DAO_CACHE_ROOT = $CacheRoot
        NIKAMI_AURORA_DAO_GENERATED_ROOT = $GeneratedRoot
        NIKAMI_AURORA_PRESENTATION_TIER = if ($SourcePresentation) { 'source' } else { 'enhanced' }
    }
    if (-not [string]::IsNullOrWhiteSpace($GameRoot)) {
        $resolvedGameRoot = [IO.Path]::GetFullPath($GameRoot)
        $requiredGameFiles = @(
            (Join-Path $resolvedGameRoot 'bin_ship\DAOrigins.exe'),
            (Join-Path $resolvedGameRoot 'packages\core\data\guiexport.erf')
        )
        foreach ($requiredGameFile in $requiredGameFiles) {
            if (-not (Test-Path -LiteralPath $requiredGameFile -PathType Leaf)) {
                throw "Dragon Age acceptance source is incomplete: $requiredGameFile"
            }
        }
        $settings.DRAGON_AGE_GODOT_GAME_ROOT = $resolvedGameRoot
    }
    if ($RunLocomotionSmoke -and -not $RunPlayableSmoke) {
        $settings.OPENDAO_FLOW_LOCOMOTION = '1'
    }
    if ($RunPlayableSmoke) {
        $settings.OPENDAO_CITY_ELF_PLAYABLE_SMOKE = '1'
        $settings.OPENDAO_ACCEPTANCE_PLAYABLE_STARTUP_FRAMES = [string]$PlayableStartupFrames
        $settings.OPENDAO_ACCEPTANCE_ALIENAGE_WARMUP_FRAMES = [string]$AlienageWarmupFrames
    }
    if (-not [string]::IsNullOrWhiteSpace($OutputVideo)) {
        $settings.OPENDAO_ACCEPTANCE_MENU_HOLD_FRAMES = [string][Math]::Round($MenuHoldSeconds * $FramesPerSecond)
        $settings.OPENDAO_ACCEPTANCE_CHARACTER_HOLD_FRAMES = [string][Math]::Round(
            $CharacterHoldSeconds * $FramesPerSecond)
        $settings.OPENDAO_ACCEPTANCE_GAMEPLAY_HOLD_FRAMES = [string][Math]::Round(
            $GameplayHoldSeconds * $FramesPerSecond)
    }
    if (-not [string]::IsNullOrWhiteSpace($DialogueChoices)) {
        $settings.OPENDAO_DIALOGUE_CHOICES = $DialogueChoices
    }
    if (-not [string]::IsNullOrWhiteSpace($DialogueChoiceHoldSeconds)) {
        $settings.OPENDAO_DIALOGUE_CHOICE_HOLD_SECONDS = $DialogueChoiceHoldSeconds
    }
    if (-not $SkipCaptures) {
        $settings.OPENDAO_MAIN_MENU_CAPTURE = Join-Path $OutputRoot 'main-menu.png'
        $settings.OPENDAO_LOADING_CAPTURE = Join-Path $OutputRoot 'loading-screen.png'
        $settings.OPENDAO_CHARACTER_DEFAULT_CAPTURE = Join-Path $OutputRoot 'character-creation-default-human.png'
        $settings.OPENDAO_CHARACTER_CREATION_CAPTURE = Join-Path $OutputRoot 'character-creation.png'
        $settings.OPENDAO_CHARACTER_APPEARANCE_CAPTURE = Join-Path $OutputRoot 'character-appearance.png'
        $settings.OPENDAO_CUTSCENE_CAPTURE = Join-Path $OutputRoot 'opening-cutscene.png'
        $settings.OPENDAO_FACEFX_CAPTURE = Join-Path $OutputRoot 'facefx-dialogue.png'
        $settings.OPENDAO_DIALOGUE_CAPTURE = Join-Path $OutputRoot 'opening-dialogue-choices.png'
        $settings.OPENDAO_DIALOGUE_LINE_CAPTURE = Join-Path $OutputRoot 'opening-dialogue-line.png'
        $settings.OPENDAO_POSE_TRANSITION_CAPTURE = Join-Path $OutputRoot 'bed-to-stand-transition.png'
        $settings.OPENDAO_STANDING_DIALOGUE_CAPTURE = Join-Path $OutputRoot 'standing-dialogue-clothed.png'
        $settings.OPENDAO_GAME_START_CAPTURE = Join-Path $OutputRoot 'game-start.png'
        if ($RunLocomotionSmoke -or $RunPlayableSmoke) {
            $settings.OPENDAO_LOCOMOTION_CAPTURE = Join-Path $OutputRoot 'locomotion-walk.png'
        }
        if ($RunPlayableSmoke) {
            $settings.OPENDAO_PLAYABLE_DESTINATION_CAPTURE = Join-Path $OutputRoot 'alienage-gameplay.png'
            $settings.OPENDAO_CITY_ELF_SKY_CAPTURE = Join-Path $OutputRoot 'alienage-sky.png'
        }
    }
    foreach ($entry in $settings.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }

    if (-not $SkipBuild) {
        & dotnet build (Join-Path $project 'Nikami.Aurora.Godot.csproj') -c Debug
        if ($LASTEXITCODE -ne 0) { throw 'Nikami Aurora Godot build failed' }
    }
    $log = Join-Path $OutputRoot 'runtime.log'
    $godotArguments = @(
        '--path', $project,
        '--rendering-method', 'forward_plus',
        '--resolution', "${ViewportWidth}x${ViewportHeight}",
        '--log-file', $log
    )
    if ($Headless) {
        $godotArguments = @('--headless') + $godotArguments
    }
    if (-not [string]::IsNullOrWhiteSpace($OutputVideo)) {
        if ($Headless) { throw 'Headless mode cannot produce the rendered proof video' }
        if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) { throw 'ffmpeg is required for MP4 output' }
        if (-not (Get-Command ffprobe -ErrorAction SilentlyContinue)) { throw 'ffprobe is required for MP4 validation' }
        $nativeMovie = Join-Path $OutputRoot `
            ("aurora-dao-native-{0}.avi" -f [Guid]::NewGuid().ToString('N'))
        $godotArguments += @('--write-movie', $nativeMovie, '--fixed-fps', [string]$FramesPerSecond,
            '--quit-after', [string]($FramesPerSecond * 600))
    }
    # Windows PowerShell wraps native stderr as ErrorRecord instances. Godot uses
    # stderr for renderer warnings, so do not let $ErrorActionPreference='Stop'
    # abort the acceptance run before its exit code and structured log are read.
    $savedErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $GodotConsolePath @godotArguments 2>&1 | Out-Null
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedErrorActionPreference
    }
    $content = Get-Content -LiteralPath $log -Raw
    $cameraTelemetry = @([regex]::Matches($content,
        'OPENDAO_(?:GAMEPLAY_FRAME_VISIBILITY|CONTINUOUS_FOLLOW_CAMERA)[^\r\n]*') |
        ForEach-Object Value)
    $capturedAreas = @($cameraTelemetry | ForEach-Object {
        $match = [regex]::Match($_, '(?:^| )area=(?<area>[^ ]+)')
        if ($match.Success) { $match.Groups['area'].Value }
    } | Sort-Object -Unique)
    $cameraCaptureEvidence = @('game-start.png', 'locomotion-walk.png',
        'alienage-gameplay.png', 'alienage-sky.png') | ForEach-Object {
        $captureFile = Join-Path $OutputRoot $_
        if (Test-Path -LiteralPath $captureFile -PathType Leaf) {
            [ordered]@{
                name = $_
                path = $captureFile
                sha256 = (Get-FileHash -LiteralPath $captureFile -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
    }
    $cameraEvidencePath = Join-Path $OutputRoot 'dao-camera-evidence-v1.json'
    $cameraEvidence = [ordered]@{
        schema = 'nikami-aurora-dao-camera-evidence-v1'
        policyScope = 'application'
        runtimeAreas = $capturedAreas
        allOtherAreas = 'runtime-unverified'
        retailMatch = 'blocked-matched-camera-telemetry-required'
        thresholds = [ordered]@{
            stableNeighboringFrames = 3
            nonClearRatio = 0.55
            luminanceStandardDeviation = 0.05
            luminanceRange = 0.18
            dominantColorRatioMaximum = 0.40
            skyFacetEdgeRatioMaximum = 0.0025
        }
        runtimeLog = $log
        runtimeLogSha256 = (Get-FileHash -LiteralPath $log -Algorithm SHA256).Hash.ToLowerInvariant()
        telemetry = $cameraTelemetry
        captures = @($cameraCaptureEvidence)
    }
    [IO.File]::WriteAllText($cameraEvidencePath,
        ($cameraEvidence | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    $requirements = [ordered]@{
        auroraRuntime = 'NIKAMI_AURORA_RUNTIME status=ready profile=dragon-age-origins scene=res://dao_boot.tscn'
        mainMenu = 'OPENDAO_MAIN_MENU_CAPTURE status=pass'
        defaultCharacterFraming = 'OPENDAO_CHARACTER_DEFAULT_FRAMING_CAPTURE status=pass'
        characterCreation = 'OPENDAO_CHARACTER_CREATION_ACCEPTANCE status=pass'
        characterAppearance = 'OPENDAO_CHARACTER_APPEARANCE_ACCEPTANCE status=pass'
        originRoute = "OPENDAO_AUTHORED_ARRIVAL source=pending-transition waypoint=$($route.waypoint)"
        abilityLoadout = "OPENDAO_CHARACTER_ABILITIES status=ready"
        retailHud = "OPENDAO_RETAIL_HUD status=ready source=gfx-display-lists"
        retailLoading = 'OPENDAO_RETAIL_LOADING_SCREEN status=ready source=load_town.gfx archive=installed-guiexport'
        retailAreaName = "OPENDAO_RETAIL_AREA_NAME status=ready"
        gameplayFrameVisibility = 'OPENDAO_GAMEPLAY_FRAME_VISIBILITY status=pass active_camera=1 subject_projected=1 subject_los=1 collision_safe=1 stable_frames=3'
        gameStart = "OPENDAO_CHARACTER_GAME_START_ACCEPTANCE status=pass character=$CharacterName area=$($route.area) player_control=1 opening_cutscene=$(if ($route.cutscene) { $route.cutscene } else { 'not-authored-for-area' })"
    }
    if ($RunLocomotionSmoke -and -not $RunPlayableSmoke) {
        $requirements.locomotion = 'OPENDAO_LOCOMOTION_TEST status=pass'
        if (-not $SkipCaptures) {
            $requirements.locomotionCapture = 'OPENDAO_LOCOMOTION_CAPTURE status=pass'
        }
    }
    if (-not $SkipCaptures) {
        $requirements.loadingCapture = 'OPENDAO_LOADING_CAPTURE status=pass'
    }
    if ($RunPlayableSmoke) {
        $requirements.houseContinuousCamera = 'OPENDAO_CONTINUOUS_FOLLOW_CAMERA status=pass segment=house-to-door'
        $requirements.exteriorContinuousCamera = 'OPENDAO_CONTINUOUS_FOLLOW_CAMERA status=pass segment=alienage-gameplay'
        $requirements.playableCityElf = 'OPENDAO_CITY_ELF_PLAYABLE_SMOKE status=pass crate_use=pass transition=pass'
        $requirements.alienageGameplay = 'OPENDAO_CITY_ELF_EXTERIOR_GAMEPLAY status=pass area=bec100ar_elven_alienage waypoint=bec100wp_from_home locomotion=pass player_control=1'
        if (-not $SkipCaptures) {
            $requirements.continuousLocomotionCapture = 'OPENDAO_LOCOMOTION_CAPTURE status=pass'
            $requirements.alienageSkyCapture = 'OPENDAO_CITY_ELF_SKY_CAPTURE status=pass'
            $requirements.alienageSkyFacetGate = 'facet_gate=pass'
        }
    }
    if ($route.cutscene) {
        $requirements.cutscene = "OPENDAO_CUTSCENE_ACCEPTANCE status=pass id=$($route.cutscene)"
    }
    if ($Origin -in @('city-elf', 'dalish-elf', 'dwarf-noble', 'circle-mage')) {
        $requirements.selectedRigCompatibility = 'OPENDAO_CINEMATIC_RIG_COMPATIBILITY status=ready'
        $requirements.selectedCinematicPlayer = 'OPENDAO_CINEMATIC_PLAYER_APPEARANCE status=ready selected_meshes='
        $requirements.selectedCinematicRig = 'rig=selected-character'
    }
    if ($Origin -eq 'city-elf') {
        $requirements.authoredAtmosphere = 'OPENDAO_AUTHORED_ATMOSPHERE status=ready background=source-atmo-sky preserved=31 exact_contract=29 additional_validated=fog_water_intensity,fog_water_cap '
        $requirements.authoredAtmosphereMapping = 'mapped=27 unsupported=fog_water_intensity,fog_water_cap,moon_rotation,skydome'
        if ($SourcePresentation) {
            $requirements.renderPipeline = 'OPENDAO_RENDER_PIPELINE status=ready method=forward_plus tier=source tonemap=linear ssao=0 ssil=0 glow=0 volumetric_clouds=0'
            $requirements.renderEnhancement = 'OPENDAO_RENDER_ENHANCEMENT status=disabled renderer=forward_plus tier=source tonemapper=linear ssao=0 ssil=0 volumetric_clouds=0 parity_claim=none'
        }
        else {
            $requirements.renderPipeline = 'OPENDAO_RENDER_PIPELINE status=ready method=forward_plus tier=enhanced tonemap=agx ssao=1 ssil=1 glow=1 volumetric_clouds=1'
            $requirements.renderEnhancement = 'OPENDAO_RENDER_ENHANCEMENT status=ready renderer=forward_plus tier=enhanced tonemapper=agx ssao=1 ssil=1 volumetric_clouds=1 parity_claim=none'
            $requirements.volumetricClouds = 'OPENDAO_VOLUMETRIC_CLOUDS status=ready source=are-atmo'
        }
        $requirements.worldMaterialInterior = 'OPENDAO_WORLD_MATERIAL_CENSUS status=partial binding_status=ready identity_status=ready layout=Den201d'
        $requirements.worldMaterialExterior = 'OPENDAO_WORLD_MATERIAL_CENSUS status=partial binding_status=ready identity_status=ready layout=den200d'
        $requirements.worldMaterialIdentity = 'payload_identity_verified='
        $requirements.worldMaterialScope = 'semantic_scope=imported-gltf-slots+terrain-contract+water-contract collision_proxies=render-suppressed'
        $requirements.worldEffectInterior = 'OPENDAO_WORLD_EFFECT_CENSUS status=ready materialized=ready parity=partial layout=den201d definitions=2 instances=3 rendered=3'
        $requirements.worldEffectExterior = 'OPENDAO_WORLD_EFFECT_CENSUS status=ready materialized=ready parity=partial layout=den200d definitions=4 instances=32 rendered=32'
        $requirements.dialogue = 'OPENDAO_DIALOGUE_FINISHED id=bec110cr_shianni status=pass'
        $requirements.linearLightEncoding = 'color_encoding=linear-radiance-to-srgb-chromaticity'
        $requirements.characterLightDomain = 'point_lights=Character_2hot,Character_1cool,Character_2cool'
        $requirements.characterLightUpload = 'source=retail-affect-domain-1 upload=raw-linear-radiance'
        $requirements.armourSkinMaterial = 'OPENDAO_CHARACTER_ARMOUR_SKIN_MATERIAL status=ready'
        $requirements.armourSkinContract = 'semantic=ArmourSkinTint shader=Ch1ArmTnt skin_mask=alpha lighting=retail-affect-domain-1'
        $requirements.possessionsCrate = 'OPENDAO_PLACEABLE_VISUAL status=ready tag=bec110ip_pc_possessions model=plc_chstwd_01_0'
        if ($RunPlayableSmoke) {
            $requirements.possessionsUse = 'OPENDAO_PLACEABLE_USE status=pass tag=bec110ip_pc_possessions handle='
            $requirements.possessionsUseCommit = 'event=7 plot=85c3d035f1274fd59849b190d64d5290 flag=2 value=1 one_shot=committed'
        }
        $requirements.shianniSkinContinuity = 'OPENDAO_CHARACTER_SKIN_CONTINUITY status=ready face=(0.94, 0.86, 0.66, 1) body=(0.94, 0.86, 0.66, 1)'
        $expectedGenderFlag = if ($Gender -eq 'female') { 259 } else { 260 }
        $expectedGenderLine = if ($Gender -eq 'female') { 66 } else { 67 }
        $requirements.characterStory = "OPENDAO_CHARACTER_STORY status=ready plot=64F06DB1ED4B49F18DF326A0B1C2D06C class_flag="
        $requirements.characterGenderStory = "gender_flag=$expectedGenderFlag"
        $requirements.dialogueGenderBranch = "OPENDAO_DIALOGUE_LINE_STARTED id=$expectedGenderLine speaker=OWNER"
        $requirements.selectedCharacterClothing = 'clothing=selected-character-city-elf-start bones=133'
        $requirements.dialogueAnimationBank = 'OPENDAO_CINEMATIC_ANIMATION_BANK status=ready'
        $requirements.dialogueActionScope = 'OPENDAO_DIALOGUE_ACTION_SCOPE state=cleared'
        $requirements.dialogueExternalCut = 'OPENDAO_DIALOGUE_EXTERNAL_CUT status=ready line=64 ref=45754 cameras=2 switches=2'
        $requirements.dialogueExternalCamera = 'OPENDAO_DIALOGUE_CAMERA_SWITCH line=64 time=1.000 camera=5'
        $requirements.dialoguePoseHandoff = 'OPENDAO_DIALOGUE_POSE_HANDOFF actor=PLAYER pose=32 resource=mh.wi_sit_grnd_lp'
        $requirements.dialogueFacialLayerMask = 'OPENDAO_ANIMATION_LAYER_MASK resource=mh.po_tw_a5 channel=body-gesture excluded_facial_tracks=19'
        $requirements.dialogueOcularLayerMask = 'OPENDAO_ANIMATION_LAYER_MASK resource=mh_o.eyelids channel=ocular excluded_nonocular_tracks=2'
        $requirements.faceMaterial = 'OPENDAO_FACE_MATERIAL status=ready source=retail-base-material'
        $requirements.characterHairMaterial = 'OPENDAO_CHARACTER_HAIR_MATERIAL status=ready surfaces=1 source=retail-hair0-psh palette=exact'
        $requirements.characterFaceMaterial = 'OPENDAO_CHARACTER_FACE_MATERIAL status=ready surfaces=1 source=retail-face0-psh palette=exact'
        $requirements.characterEyelashMaterial = 'OPENDAO_CHARACTER_EYELASH_MATERIAL status=ready surfaces=1 source=retail-eyelash0-psh state=eyelashpnch alpha_ref=20 mip_bias=-2.5'
        $requirements.characterPbrPipeline = if ($SourcePresentation) {
            'OPENDAO_CHARACTER_PBR_PIPELINE status=ready tier=source surfaces=4 shaded=0 authored_unshaded=4 variant=dao-authored-lighting layout_override=none parity_claim=none'
        }
        else {
            'OPENDAO_CHARACTER_PBR_PIPELINE status=ready tier=enhanced surfaces=4 shaded=4 authored_unshaded=0 variant=godot-pbr layout_override=none parity_claim=none'
        }
        $requirements.faceFxBasisOwner = 'OPENDAO_FACEFX_BASIS status=ready speaker=OWNER actor=elffemale mapping=XZY-reflected checks=3'
        $requirements.faceFxBasisShianni = 'OPENDAO_FACEFX_BASIS status=ready speaker=bec110cr_shianni actor=elffemale mapping=XZY-reflected checks=3'
        $requirements.cameraHandoff = 'OPENDAO_CINEMATIC_CAMERA_HANDOFF status=held source=cut target=dialogue'
        $requirements.cameraHandoffReleased = 'OPENDAO_CINEMATIC_CAMERA_HANDOFF status=released source=cut target=dialogue'
        $requirements.actorHandoffTransferred = 'OPENDAO_CINEMATIC_ACTOR_HANDOFF status=transferred source=cut target=dialogue'
        $requirements.actorHandoffAdopted = 'OPENDAO_CINEMATIC_ACTOR_HANDOFF status=adopted source=cut target=dialogue'
        $requirements.cameraOcclusion = 'OPENDAO_CINEMATIC_CAMERA_OCCLUSION status=hidden camera=5 actor=3'
        $requirements.cameraSubjectVisibility = 'OPENDAO_CINEMATIC_VISIBILITY status=pass camera=5 actor=4'
        $requirements.cameraPovAdaptation = 'OPENDAO_CINEMATIC_POV_ADAPTATION status=adapted parity=probable camera=5'
        $requirements.cameraPovRetailBlocker = 'retail_match=blocked-matched-camera-telemetry-required'
        $requirements.dialogueOutfitContinuity = 'OPENDAO_DIALOGUE_PLAYER_APPEARANCE state=standing-clothed transition=32:0 source=retail-area-equipment'
        $requirements.dialoguePoseCopy = 'OPENDAO_CINEMATIC_POSE_COPY status=ready bones=133'
        if (-not $SkipCaptures) {
            $requirements.dialogueCapture = 'OPENDAO_DIALOGUE_CAPTURE status=pass'
            $requirements.dialogueLineCapture = 'OPENDAO_DIALOGUE_LINE_CAPTURE status=pass'
            $requirements.standingCapture = 'OPENDAO_STANDING_DIALOGUE_CAPTURE status=pass'
        }
    }
    $checks = [ordered]@{}
    foreach ($requirement in $requirements.GetEnumerator()) {
        $checks[$requirement.Key] = $content.IndexOf($requirement.Value, [StringComparison]::Ordinal) -ge 0
    }
    if ($Origin -eq 'city-elf') {
        $expectedStanding = Join-Path $CacheRoot "quickplay-characters\${characterStem}.glb"
        $expectedBed = Join-Path $CacheRoot "quickplay-characters\${characterStem}-bed.glb"
        $requirements.characterIdentityExact =
            "one exact source-bound cinematic identity row for elf:${Gender}:${CharacterAppearance}"
        $identityPattern = 'OPENDAO_CINEMATIC_CHARACTER_IDENTITY status=ready character=' +
            [regex]::Escape($CharacterName) + ' selection=elf:' +
            [regex]::Escape($Gender) + ':' + [regex]::Escape($CharacterAppearance) +
            ' morph=' + [regex]::Escape($characterMorph.resource) +
            ' morph_sha256=' + [regex]::Escape($characterMorph.sha256) +
            ' provenance=(?:legacy-evidence|fresh-import) standing=' +
            [regex]::Escape($expectedStanding) + ' bed=' + [regex]::Escape($expectedBed) +
            ' identity_join=pass parity_claim=none'
        $allCharacterIdentityRows = [regex]::Matches($content,
            'OPENDAO_CINEMATIC_CHARACTER_IDENTITY status=ready[^\r\n]*')
        $exactCharacterIdentityRows = [regex]::Matches($content, $identityPattern)
        $checks.characterIdentityExact =
            $exactCharacterIdentityRows.Count -ge 1 -and
            $exactCharacterIdentityRows.Count -eq $allCharacterIdentityRows.Count
        $requirements.worldMaterialRouteCoverage =
            'all captured-area material rows require bound=surfaces, missing=0, unresolved_identity=0, and pbr_contract_ready=surfaces'
        $resolvedMaterialRows = [regex]::Matches($content,
            'OPENDAO_WORLD_MATERIAL_CENSUS status=partial binding_status=ready identity_status=ready layout=(?<layout>Den201d|den200d) surfaces=(?<surfaces>\d+) bound=(?<bound>\d+) missing=(?<missing>\d+)[^\r\n]*payload_identity_verified=(?<identity>\d+) unresolved_identity=(?<unresolved>\d+) pbr_contract_ready=(?<pbr>\d+)[^\r\n]*semantic_scope=imported-gltf-slots\+terrain-contract\+water-contract collision_proxies=render-suppressed')
        $checks.worldMaterialRouteCoverage = $resolvedMaterialRows.Count -eq 2 -and
            @($resolvedMaterialRows | Where-Object {
                [int]$_.Groups['surfaces'].Value -le 0 -or
                $_.Groups['bound'].Value -ne $_.Groups['surfaces'].Value -or
                $_.Groups['identity'].Value -ne $_.Groups['surfaces'].Value -or
                $_.Groups['pbr'].Value -ne $_.Groups['surfaces'].Value -or
                [int]$_.Groups['missing'].Value -ne 0 -or
                [int]$_.Groups['unresolved'].Value -ne 0
            }).Count -eq 0
        if (-not $SourcePresentation) {
            $requirements.applicationWideEnhancedForwardPlus =
                'every captured-area render row is enhanced Forward+ with AgX/SSAO/SSIL/glow/volumetric clouds'
            $renderRows = [regex]::Matches($content,
                'OPENDAO_RENDER_PIPELINE status=ready method=forward_plus tier=enhanced tonemap=agx ssao=1 ssil=1 glow=1 volumetric_clouds=1 layout=(?:den201d|den200d) atmosphere=source-validated')
            $checks.applicationWideEnhancedForwardPlus = $renderRows.Count -eq 2
            $requirements.zeroCharacterUnshadedFallback =
                'every enhanced character PBR row has shaded=surfaces, authored_unshaded=0, and variant=godot-pbr'
            $characterPbrRows = [regex]::Matches($content,
                'OPENDAO_CHARACTER_PBR_PIPELINE status=ready[^\r\n]*')
            $validCharacterPbrRows = [regex]::Matches($content,
                'OPENDAO_CHARACTER_PBR_PIPELINE status=ready tier=enhanced surfaces=(?<surfaces>\d+) shaded=(?<shaded>\d+) authored_unshaded=0 variant=godot-pbr layout_override=none parity_claim=none')
            $checks.zeroCharacterUnshadedFallback =
                $characterPbrRows.Count -gt 0 -and
                $validCharacterPbrRows.Count -eq $characterPbrRows.Count -and
                @($validCharacterPbrRows | Where-Object {
                    $_.Groups['surfaces'].Value -ne $_.Groups['shaded'].Value
                }).Count -eq 0
        }
        $partialEffectRows = [regex]::Matches($content,
            'OPENDAO_WORLD_EFFECT_CENSUS status=ready materialized=ready parity=partial layout=(?:den201d|den200d)[^\r\n]*distortion_skipped=(?<count>\d+)')
        $checks.worldEffectPartialEvidence = $partialEffectRows.Count -eq 2 -and
            @($partialEffectRows | Where-Object { [int]$_.Groups['count'].Value -gt 0 }).Count -eq 2
    }
    if (-not $Headless) {
        $display = [regex]::Match($content,
            'OPENDAO_DISPLAY status=applied requested=(?<requested>\d+) actual=(?<actual>\d+)')
        $checks.display = $display.Success -and
            $display.Groups['requested'].Value -eq $display.Groups['actual'].Value
    }
    $targetedDiagnostics = @(
        'ERROR:',
        'SCRIPT ERROR',
        'OPENDAO_FACEFX_FAIL',
        'OPENDAO_CUTSCENE_ACTOR_FAIL',
        'OPENDAO_DIALOGUE_FAIL',
        'OPENDAO_CHARACTER_SKIN_CONTINUITY_FAIL',
        'OPENDAO_CHARACTER_MATERIAL_FAIL',
        'OPENDAO_CINEMATIC_CHARACTER_IDENTITY status=fail',
        'OPENDAO_CINEMATIC_CHARACTER_IDENTITY status=unsupported',
        'OPENDAO_CHARACTER_APPEARANCE status=unsupported',
        'OPENDAO_GAMEPLAY_FRAME_VISIBILITY status=fail',
        'AnimationPlayer has no current animation.',
        '!is_inside_tree()'
    )
    $clean = -not ($targetedDiagnostics | Where-Object {
        $content.IndexOf($_, [StringComparison]::Ordinal) -ge 0
    })
    $passed = $exitCode -eq 0 -and $clean -and -not ($checks.Values -contains $false)
    $videoPath = ''
    $videoSha256 = ''
    if ($passed -and -not [string]::IsNullOrWhiteSpace($OutputVideo)) {
        $videoPath = $requestedVideoPath
        & ffmpeg -hide_banner -loglevel error -n -i $nativeMovie `
            -map '0:v:0' -map '0:a:0' `
            -vf "scale=${ViewportWidth}:${ViewportHeight}:flags=lanczos,setsar=1" `
            -c:v libx264 -preset medium -crf 17 -pix_fmt yuv420p `
            -c:a aac -b:a 192k -movflags +faststart $videoPath
        if ($LASTEXITCODE -ne 0) { throw 'Nikami Aurora DAO MP4 encoding failed' }
        $probe = & ffprobe -v error `
            -show_entries 'stream=codec_type,codec_name,width,height,avg_frame_rate,nb_frames:format=duration' `
            -of json $videoPath | ConvertFrom-Json
        $videoStream = @($probe.streams | Where-Object codec_type -eq 'video')[0]
        $audioStream = @($probe.streams | Where-Object codec_type -eq 'audio')[0]
        $duration = [double]$probe.format.duration
        if ($null -eq $videoStream -or $null -eq $audioStream -or
            $videoStream.codec_name -ne 'h264' -or $audioStream.codec_name -ne 'aac' -or
            [int]$videoStream.width -ne $ViewportWidth -or
            [int]$videoStream.height -ne $ViewportHeight -or
            $videoStream.avg_frame_rate -ne "$FramesPerSecond/1" -or
            $duration -lt $MinimumVideoSeconds -or $duration -gt $MaximumVideoSeconds) {
            throw 'Nikami Aurora DAO MP4 validation failed'
        }
        $videoSha256 = (Get-FileHash -LiteralPath $videoPath -Algorithm SHA256).Hash
        $videoValidated = $true
    }
    $result = [pscustomobject]@{
        status = if ($passed) { 'pass' } else { 'fail' }
        exitCode = $exitCode
        checks = $checks
        failedChecks = @($checks.GetEnumerator() |
            Where-Object { -not $_.Value } |
            ForEach-Object { "{0}: {1}" -f $_.Key, $requirements[$_.Key] })
        targetedDiagnosticsClean = $clean
        renderingMethod = 'forward_plus'
        presentationTier = if ($SourcePresentation) { 'source' } else { 'enhanced' }
        log = $log
        output = $OutputRoot
        video = $videoPath
        videoSha256 = $videoSha256
        cameraEvidence = $cameraEvidencePath
    }
    $result | Format-List
    if (-not $passed) { throw "Nikami Aurora DAO full-flow acceptance failed; inspect $log" }
}
finally {
    if ($nativeMovie -and (Test-Path -LiteralPath $nativeMovie -PathType Leaf)) {
        Remove-Item -LiteralPath $nativeMovie -Force
    }
    if ($requestedVideoPath -and -not $videoValidated -and
        (Test-Path -LiteralPath $requestedVideoPath -PathType Leaf)) {
        Remove-Item -LiteralPath $requestedVideoPath -Force
    }
    foreach ($name in $variables) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
    }
}
