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
    if ($RunLocomotionSmoke) {
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
        if ($RunLocomotionSmoke) {
            $settings.OPENDAO_LOCOMOTION_CAPTURE = Join-Path $OutputRoot 'locomotion-walk.png'
        }
        if ($RunPlayableSmoke) {
            $settings.OPENDAO_PLAYABLE_DESTINATION_CAPTURE = Join-Path $OutputRoot 'alienage-gameplay.png'
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
        '--rendering-method', 'gl_compatibility',
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
        gameStart = "OPENDAO_CHARACTER_GAME_START_ACCEPTANCE status=pass character=$CharacterName area=$($route.area) player_control=1 opening_cutscene=$(if ($route.cutscene) { $route.cutscene } else { 'not-authored-for-area' })"
    }
    if ($RunLocomotionSmoke) {
        $requirements.locomotion = 'OPENDAO_LOCOMOTION_TEST status=pass'
        if (-not $SkipCaptures) {
            $requirements.locomotionCapture = 'OPENDAO_LOCOMOTION_CAPTURE status=pass'
        }
    }
    if (-not $SkipCaptures) {
        $requirements.loadingCapture = 'OPENDAO_LOADING_CAPTURE status=pass'
    }
    if ($RunPlayableSmoke) {
        $requirements.playableCityElf = 'OPENDAO_CITY_ELF_PLAYABLE_SMOKE status=pass crate_use=pass transition=pass'
        $requirements.alienageGameplay = 'OPENDAO_CITY_ELF_EXTERIOR_GAMEPLAY status=pass area=bec100ar_elven_alienage waypoint=bec100wp_from_home locomotion=pass player_control=1'
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
        $requirements.dialogue = 'OPENDAO_DIALOGUE_FINISHED id=bec110cr_shianni status=pass'
        $requirements.linearLightEncoding = 'color_encoding=linear-radiance-to-srgb-chromaticity'
        $requirements.characterLightDomain = 'point_lights=Character_2hot,Character_1cool,Character_2cool'
        $requirements.characterLightUpload = 'source=retail-affect-domain-1 upload=raw-linear-radiance'
        $requirements.armourSkinMaterial = 'OPENDAO_CHARACTER_ARMOUR_SKIN_MATERIAL status=ready'
        $requirements.armourSkinContract = 'semantic=ArmourSkinTint shader=Ch1ArmTnt skin_mask=alpha lighting=retail-affect-domain-1'
        $requirements.possessionsCrate = 'OPENDAO_PLACEABLE_VISUAL status=ready tag=bec110ip_pc_possessions model=plc_chstwd_01_0'
        $requirements.possessionsHighlight = 'OPENDAO_PLACEABLE_HIGHLIGHT status=active tag=bec110ip_pc_possessions'
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
        $requirements.characterMaterialContract = 'OPENDAO_CHARACTER_MATERIAL_CONTRACT status=restored kind=hair'
        $requirements.faceFxBasisOwner = 'OPENDAO_FACEFX_BASIS status=ready speaker=OWNER actor=elffemale mapping=XZY-reflected checks=3'
        $requirements.faceFxBasisShianni = 'OPENDAO_FACEFX_BASIS status=ready speaker=bec110cr_shianni actor=elffemale mapping=XZY-reflected checks=3'
        $requirements.cameraHandoff = 'OPENDAO_CINEMATIC_CAMERA_HANDOFF status=held source=cut target=dialogue'
        $requirements.cameraHandoffReleased = 'OPENDAO_CINEMATIC_CAMERA_HANDOFF status=released source=cut target=dialogue'
        $requirements.actorHandoffTransferred = 'OPENDAO_CINEMATIC_ACTOR_HANDOFF status=transferred source=cut target=dialogue'
        $requirements.actorHandoffAdopted = 'OPENDAO_CINEMATIC_ACTOR_HANDOFF status=adopted source=cut target=dialogue'
        $requirements.cameraOcclusion = 'OPENDAO_CINEMATIC_CAMERA_OCCLUSION status=hidden camera=5 actor=3'
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
    if (-not $Headless) {
        $display = [regex]::Match($content,
            'OPENDAO_DISPLAY status=applied requested=(?<requested>\d+) actual=(?<actual>\d+)')
        $checks.display = $display.Success -and
            $display.Groups['requested'].Value -eq $display.Groups['actual'].Value
    }
    $targetedDiagnostics = @(
        'OPENDAO_FACEFX_FAIL',
        'OPENDAO_CUTSCENE_ACTOR_FAIL',
        'OPENDAO_DIALOGUE_FAIL',
        'OPENDAO_CHARACTER_SKIN_CONTINUITY_FAIL',
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
        log = $log
        output = $OutputRoot
        video = $videoPath
        videoSha256 = $videoSha256
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
