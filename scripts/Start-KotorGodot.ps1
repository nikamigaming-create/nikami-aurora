[CmdletBinding()]
param(
    [string]$Manifest,
    [ValidateSet('kotor', 'kotor2')]
    [string]$Profile = 'kotor',
    [ValidatePattern('^[A-Za-z0-9_]{1,16}$')]
    [string]$Module = "end_m01aa",
    [string]$Godot,
    [string]$CapturePath,
    [string]$CaptureDialogueNode,
    [string]$CaptureCreature,
    [string]$CaptureCreatureEffectAnimation,
    [string]$CaptureCreatureEffectAnchor,
    [int]$CaptureFrame = 0,
    [int]$DialogueChoice = -1,
    [double]$TestMoveMeters = 0,
    [int]$TestPlayerXp = -1,
    [string]$TestPlayerAnimation,
    [switch]$OpenFirstDoor,
    [switch]$OpenFirstLocker,
    [switch]$EquipOpeningGear,
    [switch]$TestTutorialXpChain,
    [switch]$TestFirstCorridorTrigger,
    [switch]$TestFirstCorridorTransmission,
    [switch]$TestFirstEncounter,

    [switch]$TestFirstCombat,
    [switch]$ShowcaseRoute,
    [switch]$GenericWorldShowcase,
    [switch]$ExitOnShowcaseComplete,
    [switch]$SkipOpeningDialogue,
    [switch]$OpenXR,
    [switch]$OpenXRSimulator,
    [string]$OpenXRRuntimeJson,
    [string]$MoviePath,
    [ValidateRange(1, 240)]
    [int]$MovieFps = 60,
    [string]$GodotStdoutPath,
    [string]$GodotStderrPath,
    [ValidateRange(0, 7200)]
    [int]$TimeoutSeconds = 0,
    [ValidateRange(65536, 16777216)]
    [int]$MaximumLogCharacters = 2097152,
    [switch]$CleanCapture,
    [switch]$SourcePresentation,
    [switch]$LipSyncCloseup,
    [switch]$P2pEmitterCloseup,
    [switch]$EquipmentCloseup,
    [switch]$ChairCloseup,
    [switch]$XrBodyLookDown,
    [switch]$LoadingScreenCapture,
    [switch]$HudScreen,
    [switch]$InventoryScreen,
    [switch]$EquipmentScreen,
    [switch]$TestEquipmentMenuTransaction,
    [switch]$TestFlatMenuNavigation,
    [switch]$TestInventoryQuestFilter,
    [switch]$TestInventoryScroll,
    [switch]$TestInventoryPartySelection,
    [switch]$TestXrTrackedRig,
    [switch]$TestXrDialogueControls,
    [switch]$TestXrMovement,
    [switch]$TestXrSnapTurn,
    [switch]$CaptureAndExit
)

function Write-BoundedRuntimeLog {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [AllowEmptyString()][string]$Text,
        [Parameter(Mandatory = $true)][int]$MaximumCharacters
    )

    if ($Text.Length -le $MaximumCharacters) {
        [IO.File]::WriteAllText($Path, $Text)
        return
    }
    $half = [Math]::Max(1, [int][Math]::Floor($MaximumCharacters / 2))
    $omitted = $Text.Length - 2 * $half
    $bounded = $Text.Substring(0, $half) + [Environment]::NewLine +
        "NIKAMI_AURORA_LOG status=truncated omitted_characters=$omitted" +
        [Environment]::NewLine + $Text.Substring($Text.Length - $half)
    [IO.File]::WriteAllText($Path, $bounded)
}

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$hadXrRuntimeJson = Test-Path Env:XR_RUNTIME_JSON
$previousXrRuntimeJson = $env:XR_RUNTIME_JSON
if ([string]::IsNullOrWhiteSpace($Manifest)) {
    $Module = $Module.ToLowerInvariant()
    $Manifest = Join-Path $repo "local\$Profile\$Module\module-manifest.json"
}
if (-not (Test-Path -LiteralPath $Manifest -PathType Leaf)) {
    throw "Module manifest not found: $Manifest. Run scripts/Import-KotorModule.ps1 first."
}
$manifestContract = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
if ($manifestContract.schema -ne 'nikami-aurora-kotor-module-v1' -or
    $manifestContract.profileId -notin @('kotor', 'kotor2')) {
    throw "Unsupported Odyssey module manifest contract: $Manifest"
}
if ($PSBoundParameters.ContainsKey('Profile') -and
    $Profile -ne [string]$manifestContract.profileId) {
    throw "Requested profile $Profile does not match manifest profile $($manifestContract.profileId)."
}
$Profile = [string]$manifestContract.profileId
$manifestModule = [string]$manifestContract.module
if ($manifestModule -notmatch '^[A-Za-z0-9_]{1,16}$') {
    throw "KOTOR manifest has an invalid module identifier: $manifestModule"
}
$manifestModule = $manifestModule.ToLowerInvariant()
if ($PSBoundParameters.ContainsKey('Module') -and
    $Module.ToLowerInvariant() -ne $manifestModule) {
    throw "Requested module $Module does not match manifest module $manifestModule."
}
$isEndarModule = $manifestModule -eq 'end_m01aa'
$expectedContentMode = if ($isEndarModule) { 'endar-opening' } else { 'generic-world' }
if ([string]$manifestContract.contentMode -ne $expectedContentMode -or
    ($isEndarModule -and $null -eq $manifestContract.firstEncounter) -or
    (-not $isEndarModule -and $null -ne $manifestContract.firstEncounter)) {
    throw "KOTOR manifest content identity is inconsistent: module=$manifestModule " +
          "mode=$($manifestContract.contentMode)."
}
$reportedSourceAbsences = @($manifestContract.unresolvedTextureReferences).Count
if ([string]$manifestContract.missingSourceAssetPolicy -ne
        'source-absence-report-no-fabrication-v1' -or
    $reportedSourceAbsences -ne
        [int]$manifestContract.counts.unresolvedTextureReferences) {
    throw "KOTOR missing-source-asset policy/report inventory is inconsistent."
}
$endarOnlyRequested =
    $OpenFirstDoor -or $OpenFirstLocker -or $EquipOpeningGear -or
    $TestTutorialXpChain -or $TestFirstCorridorTrigger -or
    $TestFirstCorridorTransmission -or $TestFirstEncounter -or $TestFirstCombat -or
    $ShowcaseRoute -or $ExitOnShowcaseComplete -or $LipSyncCloseup -or
    $EquipmentCloseup -or $ChairCloseup -or $TestEquipmentMenuTransaction -or
    $TestFlatMenuNavigation -or $TestInventoryQuestFilter -or
    $TestInventoryScroll -or $TestInventoryPartySelection -or
    $TestXrDialogueControls
if ($endarOnlyRequested -and -not $isEndarModule) {
    throw "Endar story/camera/UI automation cannot run for generic module $manifestModule."
}
if ($GenericWorldShowcase -and $isEndarModule) {
    throw '-GenericWorldShowcase requires a generic-world module manifest.'
}
if ($GenericWorldShowcase -and ($OpenXR -or $OpenXRSimulator)) {
    throw '-GenericWorldShowcase is a deterministic desktop capture route.'
}
$referenceWidth = [int]$manifestContract.ui.hud.layout.extent.width
$referenceHeight = [int]$manifestContract.ui.hud.layout.extent.height
$configuredCaptureFrame = [int]$manifestContract.runtimeConfiguration.automation.sceneReadyFrame
if ($referenceWidth -le 0 -or $referenceHeight -le 0 -or $configuredCaptureFrame -le 0) {
    throw "Module manifest has no valid runtime/UI reference configuration. Re-import it."
}
if ($CaptureFrame -le 0) {
    $CaptureFrame = $configuredCaptureFrame
}
if ([string]::IsNullOrWhiteSpace($Godot)) {
    $command = Get-Command "Godot_v4.7.1-stable_mono_win64.exe" -ErrorAction SilentlyContinue
    if ($command) {
        $Godot = $command.Source
    }
    else {
        $bundledGodot = Join-Path $repo `
            'local\tools\godot-4.7.1\Godot_v4.7.1-stable_mono_win64\Godot_v4.7.1-stable_mono_win64.exe'
        if (-not (Test-Path -LiteralPath $bundledGodot -PathType Leaf)) {
            throw "Godot 4.7 .NET was not found on PATH or in local/tools. " +
                  "Pass -Godot explicitly."
        }
        $Godot = $bundledGodot
    }
}
$Godot = [IO.Path]::GetFullPath($Godot)
if (-not (Test-Path -LiteralPath $Godot -PathType Leaf)) {
    throw "Godot executable not found: $Godot"
}
$godotVersion = (& $Godot --version 2>&1 | Select-Object -First 1).ToString().Trim()
if ($godotVersion -notmatch '^4\.7\..*\.mono\.') {
    throw "KOTOR runtime requires Godot 4.7 .NET; '$Godot' reports " +
          "'$godotVersion'."
}
$resolvedMoviePath = $null
if (-not [string]::IsNullOrWhiteSpace($MoviePath)) {
    if (-not $ShowcaseRoute -and -not $TestFirstEncounter -and
        -not $GenericWorldShowcase -and -not $CaptureAndExit -and
        [string]::IsNullOrWhiteSpace($CaptureCreatureEffectAnimation)) {
        throw "-MoviePath requires a bounded exit route such as -CaptureAndExit, " +
              "-ShowcaseRoute, -GenericWorldShowcase, -TestFirstEncounter, or " +
              "-CaptureCreatureEffectAnimation."
    }
    if ($OpenXR -and -not $OpenXRSimulator) {
        throw "-MoviePath supports desktop or -OpenXRSimulator recording; " +
              "a live headset recording is not deterministic."
    }
    $extension = [IO.Path]::GetExtension($MoviePath).ToLowerInvariant()
    if ($extension -notin @('.avi', '.ogv')) {
        throw "Godot Movie Maker output must use .avi or .ogv."
    }
    $resolvedMoviePath = [IO.Path]::GetFullPath($MoviePath)
    $movieDirectory = Split-Path -Parent $resolvedMoviePath
    if (-not [string]::IsNullOrWhiteSpace($movieDirectory)) {
        New-Item -ItemType Directory -Path $movieDirectory -Force | Out-Null
    }
    $env:NIKAMI_AURORA_SHOWCASE_EXIT_ON_COMPLETE = "1"
}
if ([string]::IsNullOrWhiteSpace($GodotStdoutPath) -xor
    [string]::IsNullOrWhiteSpace($GodotStderrPath)) {
    throw '-GodotStdoutPath and -GodotStderrPath must be supplied together.'
}
$redirectGodotOutput = -not [string]::IsNullOrWhiteSpace($GodotStdoutPath)
$resolvedGodotStdoutPath = $null
$resolvedGodotStderrPath = $null
if ($redirectGodotOutput) {
    $resolvedGodotStdoutPath = [IO.Path]::GetFullPath($GodotStdoutPath)
    $resolvedGodotStderrPath = [IO.Path]::GetFullPath($GodotStderrPath)
}

$env:NIKAMI_AURORA_PROFILE = $Profile
$env:NIKAMI_AURORA_MODULE_MANIFEST = (Resolve-Path -LiteralPath $Manifest).Path
if (-not [string]::IsNullOrWhiteSpace($CapturePath)) {
    $env:NIKAMI_AURORA_CAPTURE = [IO.Path]::GetFullPath($CapturePath)
    $env:NIKAMI_AURORA_CAPTURE_FRAME = $CaptureFrame.ToString(
        [Globalization.CultureInfo]::InvariantCulture)
}
if (-not [string]::IsNullOrWhiteSpace($CaptureDialogueNode)) {
    $env:NIKAMI_AURORA_CAPTURE_DIALOGUE_NODE = $CaptureDialogueNode
}
if (-not [string]::IsNullOrWhiteSpace($CaptureCreature)) {
    if ($CaptureCreature -notmatch '^[A-Za-z0-9_]{1,32}$') {
        throw "Capture creature identity is invalid: $CaptureCreature"
    }
    $env:NIKAMI_AURORA_CAPTURE_CREATURE = $CaptureCreature.ToLowerInvariant()
}
if (-not [string]::IsNullOrWhiteSpace($CaptureCreatureEffectAnimation)) {
    if ([string]::IsNullOrWhiteSpace($CaptureCreature)) {
        throw '-CaptureCreatureEffectAnimation requires -CaptureCreature.'
    }
    if ($CaptureCreatureEffectAnimation -notmatch '^[A-Za-z0-9_]{1,32}$') {
        throw "Capture creature effect animation is invalid: $CaptureCreatureEffectAnimation"
    }
    $env:NIKAMI_AURORA_CAPTURE_CREATURE_EFFECT_ANIMATION =
        $CaptureCreatureEffectAnimation.ToLowerInvariant()
}
if (-not [string]::IsNullOrWhiteSpace($CaptureCreatureEffectAnchor)) {
    if ([string]::IsNullOrWhiteSpace($CaptureCreatureEffectAnimation)) {
        throw '-CaptureCreatureEffectAnchor requires -CaptureCreatureEffectAnimation.'
    }
    if ($CaptureCreatureEffectAnchor -notmatch '^[A-Za-z0-9_:.-]{1,64}$') {
        throw "Capture creature effect anchor is invalid: $CaptureCreatureEffectAnchor"
    }
    $env:NIKAMI_AURORA_CAPTURE_CREATURE_EFFECT_ANCHOR =
        $CaptureCreatureEffectAnchor.ToLowerInvariant()
}
if ($CaptureAndExit) {
    $env:NIKAMI_AURORA_CAPTURE_EXIT = "1"
}
if ($DialogueChoice -ge 0) {
    $env:NIKAMI_AURORA_DIALOGUE_CHOICE = $DialogueChoice.ToString()
}
if ($TestMoveMeters -ne 0) {
    $env:NIKAMI_AURORA_TEST_MOVE_METERS = $TestMoveMeters.ToString(
        [Globalization.CultureInfo]::InvariantCulture)
}
if ($TestPlayerXp -ge 0) {
    $env:NIKAMI_AURORA_TEST_PLAYER_XP = $TestPlayerXp.ToString(
        [Globalization.CultureInfo]::InvariantCulture)
}
if ($OpenFirstDoor) {
    $env:NIKAMI_AURORA_TEST_OPEN_DOOR = "1"
}
if ($OpenFirstLocker) {
    $env:NIKAMI_AURORA_TEST_OPEN_LOCKER = "1"
}
if ($EquipOpeningGear) {
    $env:NIKAMI_AURORA_TEST_EQUIP_OPENING_GEAR = "1"
}
if ($TestTutorialXpChain) {
    $env:NIKAMI_AURORA_TEST_TUTORIAL_XP_CHAIN = "1"
}
if ($TestFirstCorridorTrigger) {
    $env:NIKAMI_AURORA_TEST_FIRST_CORRIDOR_TRIGGER = "1"
    $env:NIKAMI_AURORA_SKIP_OPENING_DIALOGUE = "1"
}
if ($TestFirstCorridorTransmission) {
    $env:NIKAMI_AURORA_TEST_FIRST_CORRIDOR_TRIGGER = "1"
    $env:NIKAMI_AURORA_TEST_FIRST_CORRIDOR_TRANSMISSION = "1"
    $env:NIKAMI_AURORA_SKIP_OPENING_DIALOGUE = "1"
}
if ($TestFirstEncounter) {
    $env:NIKAMI_AURORA_TEST_FIRST_ENCOUNTER = "1"
    $env:NIKAMI_AURORA_SKIP_OPENING_DIALOGUE = "1"
}
if ($ShowcaseRoute) {
    $env:NIKAMI_AURORA_SHOWCASE_ROUTE = "1"
}
if ($TestFirstCombat) {
    $env:NIKAMI_AURORA_TEST_FIRST_COMBAT = "1"
    $env:NIKAMI_AURORA_SKIP_OPENING_DIALOGUE = "1"
}
if ($GenericWorldShowcase) {
    $env:NIKAMI_AURORA_GENERIC_WORLD_SHOWCASE = "1"
    $env:NIKAMI_AURORA_SHOWCASE_EXIT_ON_COMPLETE = "1"
}
if ($ExitOnShowcaseComplete) {
    if (-not $ShowcaseRoute) {
        throw "-ExitOnShowcaseComplete requires -ShowcaseRoute."
    }
    $env:NIKAMI_AURORA_SHOWCASE_EXIT_ON_COMPLETE = "1"
}
if (-not [string]::IsNullOrWhiteSpace($TestPlayerAnimation)) {
    $env:NIKAMI_AURORA_TEST_PLAYER_ANIMATION = $TestPlayerAnimation
}
if ($SkipOpeningDialogue) {
    $env:NIKAMI_AURORA_SKIP_OPENING_DIALOGUE = "1"
}
if ($OpenXR) {
    $env:NIKAMI_AURORA_OPENXR = "1"
}
if ($OpenXRSimulator) {
    $OpenXR = $true
    if ([string]::IsNullOrWhiteSpace($OpenXRRuntimeJson)) {
        $simulatorRoot = 'C:\Program Files\MetaXRSimulator'
        $OpenXRRuntimeJson = Get-ChildItem -LiteralPath $simulatorRoot -Directory `
            -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'meta_openxr_simulator.json' } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1
    }
    if ([string]::IsNullOrWhiteSpace($OpenXRRuntimeJson) -or
        -not (Test-Path -LiteralPath $OpenXRRuntimeJson -PathType Leaf)) {
        throw "Meta OpenXR Simulator runtime JSON was not found."
    }
    $env:XR_RUNTIME_JSON = (Resolve-Path -LiteralPath $OpenXRRuntimeJson).Path
    $env:NIKAMI_AURORA_OPENXR = "1"
    $env:NIKAMI_AURORA_OPENXR_EXPECT_ACTIVE = "1"
    $env:NIKAMI_AURORA_XR_SPECTATOR = "1"
}
if ($CleanCapture) {
    $env:NIKAMI_AURORA_CAPTURE_CLEAN = "1"
}
$env:NIKAMI_AURORA_PRESENTATION_TIER = if ($SourcePresentation) {
    "source"
}
else {
    "enhanced"
}
if ($LipSyncCloseup) {
    $env:NIKAMI_AURORA_CAPTURE_LIP_CLOSEUP = "1"
}
if ($P2pEmitterCloseup) {
    if ([string]::IsNullOrWhiteSpace($CapturePath)) {
        throw "-P2pEmitterCloseup requires -CapturePath."
    }
    $env:NIKAMI_AURORA_CAPTURE_P2P_EMITTER_CLOSEUP = "1"
}
if ($EquipmentCloseup) {
    $env:NIKAMI_AURORA_CAPTURE_PLAYER_EQUIPMENT_CLOSEUP = "1"
}
if ($ChairCloseup) {
    $env:NIKAMI_AURORA_CAPTURE_CHAIR_CLOSEUP = "1"
}
if ($XrBodyLookDown) {
    if (-not $OpenXRSimulator) {
        throw "-XrBodyLookDown requires -OpenXRSimulator."
    }
    $env:NIKAMI_AURORA_CAPTURE_XR_BODY_LOOKDOWN = "1"
}
if ($LoadingScreenCapture) {
    if ($OpenXR -or $OpenXRSimulator) {
        throw "-LoadingScreenCapture is a flat presentation gate."
    }
    if ([string]::IsNullOrWhiteSpace($CapturePath)) {
        throw "-LoadingScreenCapture requires -CapturePath."
    }
    $env:NIKAMI_AURORA_CAPTURE_LOADING_SCREEN = "1"
}
if ($HudScreen) {
    if ($OpenXR -or $OpenXRSimulator) {
        throw "-HudScreen is a flat presentation gate."
    }
    if ([string]::IsNullOrWhiteSpace($CapturePath)) {
        throw "-HudScreen requires -CapturePath."
    }
    $env:NIKAMI_AURORA_SKIP_OPENING_DIALOGUE = "1"
}
if ($InventoryScreen) {
    if ($OpenXR -or $OpenXRSimulator) {
        throw "-InventoryScreen is a flat presentation gate."
    }
    $env:NIKAMI_AURORA_TEST_INVENTORY_SCREEN = "1"
    if ($isEndarModule) {
        $env:NIKAMI_AURORA_TEST_OPEN_LOCKER = "1"
    }
    $env:NIKAMI_AURORA_SKIP_OPENING_DIALOGUE = "1"
}
if ($TestInventoryQuestFilter) {
    if (-not $InventoryScreen) {
        throw "-TestInventoryQuestFilter requires -InventoryScreen."
    }
    $env:NIKAMI_AURORA_TEST_INVENTORY_QUEST_FILTER = "1"
}
if ($TestInventoryScroll) {
    if (-not $InventoryScreen) {
        throw "-TestInventoryScroll requires -InventoryScreen."
    }
    $env:NIKAMI_AURORA_TEST_INVENTORY_SCROLL_REPEAT = "1"
}
if ($TestInventoryPartySelection) {
    if (-not $InventoryScreen) {
        throw "-TestInventoryPartySelection requires -InventoryScreen."
    }
    $env:NIKAMI_AURORA_TEST_INVENTORY_PARTY_SELECTION = "1"
}
if ($EquipmentScreen) {
    if ($OpenXR -or $OpenXRSimulator) {
        throw "-EquipmentScreen is a flat presentation gate."
    }
    $env:NIKAMI_AURORA_TEST_EQUIPMENT_SCREEN = "1"
    if ($isEndarModule) {
        $env:NIKAMI_AURORA_TEST_OPEN_LOCKER = "1"
    }
    $env:NIKAMI_AURORA_SKIP_OPENING_DIALOGUE = "1"
}
if ($TestEquipmentMenuTransaction) {
    if (-not $EquipmentScreen) {
        throw "-TestEquipmentMenuTransaction requires -EquipmentScreen."
    }
    $env:NIKAMI_AURORA_TEST_EQUIPMENT_MENU_TRANSACTION = "1"
}
if ($TestFlatMenuNavigation) {
    if (-not $EquipmentScreen) {
        throw "-TestFlatMenuNavigation requires -EquipmentScreen."
    }
    $env:NIKAMI_AURORA_TEST_FLAT_MENU_NAVIGATION = "1"
}
if ($LoadingScreenCapture -or $HudScreen -or $InventoryScreen -or $EquipmentScreen) {
    $env:NIKAMI_AURORA_FLAT_UI_REFERENCE_VIEWPORT =
        "$($referenceWidth)x$($referenceHeight)"
}
if ($TestXrTrackedRig) {
    if (-not $OpenXRSimulator) {
        throw "-TestXrTrackedRig requires -OpenXRSimulator."
    }
    $env:NIKAMI_AURORA_TEST_XR_TRACKED_RIG = "1"
}
if ($TestXrDialogueControls) {
    if (-not $OpenXRSimulator) {
        throw "-TestXrDialogueControls requires -OpenXRSimulator."
    }
    $env:NIKAMI_AURORA_TEST_XR_DIALOGUE_CONTROLS = "1"
}
if ($TestXrMovement) {
    if (-not $OpenXRSimulator) {
        throw "-TestXrMovement requires -OpenXRSimulator."
    }
    $env:NIKAMI_AURORA_TEST_XR_MOVEMENT = "1"
}
if ($TestXrSnapTurn) {
    if (-not $OpenXRSimulator) {
        throw "-TestXrSnapTurn requires -OpenXRSimulator."
    }
    $env:NIKAMI_AURORA_TEST_XR_SNAP_TURN = "1"
}

try {
    & dotnet build (Join-Path $repo "godot\Nikami.Aurora.Godot.csproj") --configuration Debug
    if ($LASTEXITCODE -ne 0) {
        throw "Godot C# build failed with code $LASTEXITCODE"
    }
    $godotArguments = @("--path", (Join-Path $repo "godot"))
    if ($LoadingScreenCapture -or $HudScreen -or $InventoryScreen -or $EquipmentScreen) {
        # Use the imported HUD extent so acceptance cannot drift from the
        # player's owned source contract when a different layout is selected.
        $godotArguments += @(
            "--windowed",
            "--resolution",
            "$($referenceWidth)x$($referenceHeight)"
        )
    }
    if ($OpenXR) {
        $godotArguments += @("--xr-mode", "on")
    }
    if (-not [string]::IsNullOrWhiteSpace($resolvedMoviePath)) {
        $godotArguments += @(
            "--write-movie", $resolvedMoviePath,
            "--fixed-fps", $MovieFps.ToString([Globalization.CultureInfo]::InvariantCulture)
        )
    }
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Godot
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $redirectGodotOutput
    $startInfo.RedirectStandardError = $redirectGodotOutput
    foreach ($argument in $godotArguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }
    $godotProcess = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $godotProcess) {
        throw "Godot process could not be started."
    }
    if ($redirectGodotOutput) {
        $stdoutTask = $godotProcess.StandardOutput.ReadToEndAsync()
        $stderrTask = $godotProcess.StandardError.ReadToEndAsync()
    }
    $timedOut = $false
    if ($TimeoutSeconds -gt 0) {
        $timedOut = -not $godotProcess.WaitForExit($TimeoutSeconds * 1000)
        if ($timedOut) {
            $godotProcess.Kill($true)
            $godotProcess.WaitForExit()
        }
    }
    else {
        $godotProcess.WaitForExit()
    }
    if ($redirectGodotOutput) {
        Write-BoundedRuntimeLog -Path $resolvedGodotStdoutPath `
            -Text $stdoutTask.GetAwaiter().GetResult() `
            -MaximumCharacters $MaximumLogCharacters
        Write-BoundedRuntimeLog -Path $resolvedGodotStderrPath `
            -Text $stderrTask.GetAwaiter().GetResult() `
            -MaximumCharacters $MaximumLogCharacters
    }
    if ($timedOut) {
        throw "Godot exceeded the bounded runtime timeout of $TimeoutSeconds seconds."
    }
    if ($godotProcess.ExitCode -ne 0) {
        throw "Godot exited with code $($godotProcess.ExitCode)"
    }
    if ($SourcePresentation) {
        if ($redirectGodotOutput) {
            $sourceEvidence = (Get-Content -LiteralPath $resolvedGodotStdoutPath -Raw) +
                              [Environment]::NewLine +
                              (Get-Content -LiteralPath $resolvedGodotStderrPath -Raw)
        }
        else {
            $runtimeLog = Join-Path $env:APPDATA `
                'Godot\app_userdata\Nikami Aurora\logs\godot.log'
            if (-not (Test-Path -LiteralPath $runtimeLog -PathType Leaf)) {
                throw 'KOTOR source-presentation runtime log is missing.'
            }
            $sourceEvidence = Get-Content -LiteralPath $runtimeLog -Raw
        }
        $requiredSourceTransfer =
            'NIKAMI_AURORA_LIGHTMAP_TRANSFER status=ready tier=source ' +
            'formula=surface-times-clamped-lightmap diffuse_weight=0.00 ' +
            'baked_weight=1.00 dynamic_ambient_weight=0.00 dynamic_lights=0 ' +
            'double_light=0'
        if ($sourceEvidence.IndexOf(
                $requiredSourceTransfer, [StringComparison]::Ordinal) -lt 0) {
            throw 'KOTOR source-presentation lightmap transfer evidence is missing.'
        }
    }
}
finally {
    Remove-Item Env:NIKAMI_AURORA_PROFILE -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_MODULE_MANIFEST -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE_FRAME -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE_DIALOGUE_NODE -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE_CREATURE -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE_CREATURE_EFFECT_ANIMATION `
        -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE_CREATURE_EFFECT_ANCHOR `
        -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE_EXIT -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_DIALOGUE_CHOICE -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_MOVE_METERS -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_PLAYER_XP -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_PLAYER_ANIMATION -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_OPEN_DOOR -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_OPEN_LOCKER -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_EQUIP_OPENING_GEAR -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_TUTORIAL_XP_CHAIN -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_FIRST_CORRIDOR_TRIGGER -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_FIRST_CORRIDOR_TRANSMISSION -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_FIRST_ENCOUNTER -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_FIRST_COMBAT -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_SHOWCASE_ROUTE -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_GENERIC_WORLD_SHOWCASE -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_SHOWCASE_EXIT_ON_COMPLETE -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_SKIP_OPENING_DIALOGUE -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_OPENXR -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_OPENXR_EXPECT_ACTIVE -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_XR_SPECTATOR -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE_CLEAN -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_PRESENTATION_TIER -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE_LIP_CLOSEUP -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE_P2P_EMITTER_CLOSEUP -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE_PLAYER_EQUIPMENT_CLOSEUP -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE_CHAIR_CLOSEUP -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE_XR_BODY_LOOKDOWN -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE_LOADING_SCREEN -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_INVENTORY_SCREEN -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_INVENTORY_QUEST_FILTER -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_INVENTORY_SCROLL_REPEAT -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_INVENTORY_PARTY_SELECTION -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_EQUIPMENT_SCREEN -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_EQUIPMENT_MENU_TRANSACTION -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_FLAT_MENU_NAVIGATION -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_FLAT_UI_REFERENCE_VIEWPORT -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_XR_TRACKED_RIG -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_XR_DIALOGUE_CONTROLS -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_XR_MOVEMENT -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_XR_SNAP_TURN -ErrorAction SilentlyContinue
    if ($hadXrRuntimeJson) {
        $env:XR_RUNTIME_JSON = $previousXrRuntimeJson
    }
    else {
        Remove-Item Env:XR_RUNTIME_JSON -ErrorAction SilentlyContinue
    }
}
