[CmdletBinding()]
param(
    [string]$Manifest,
    [string]$Godot,
    [string]$CapturePath,
    [string]$CaptureDialogueNode,
    [int]$CaptureFrame = 60,
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
    [switch]$ShowcaseRoute,
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
    [switch]$CleanCapture,
    [switch]$LipSyncCloseup,
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
    [switch]$CaptureAndExit
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$hadXrRuntimeJson = Test-Path Env:XR_RUNTIME_JSON
$previousXrRuntimeJson = $env:XR_RUNTIME_JSON
if ([string]::IsNullOrWhiteSpace($Manifest)) {
    $Manifest = Join-Path $repo "local\kotor\end_m01aa\module-manifest.json"
}
if (-not (Test-Path -LiteralPath $Manifest -PathType Leaf)) {
    throw "Module manifest not found: $Manifest. Run scripts/Import-KotorModule.ps1 first."
}
if ([string]::IsNullOrWhiteSpace($Godot)) {
    $command = Get-Command "Godot_v4.7.1-stable_mono_win64.exe" -ErrorAction SilentlyContinue
    if (-not $command) {
        throw "Godot 4.7.1 .NET was not found on PATH. Pass -Godot explicitly."
    }
    $Godot = $command.Source
}
$resolvedMoviePath = $null
if (-not [string]::IsNullOrWhiteSpace($MoviePath)) {
    if (-not $OpenXRSimulator -or
        (-not $ShowcaseRoute -and -not $TestFirstEncounter)) {
        throw "-MoviePath requires -OpenXRSimulator and either " +
              "-ShowcaseRoute or -TestFirstEncounter."
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

$env:NIKAMI_AURORA_MODULE_MANIFEST = (Resolve-Path -LiteralPath $Manifest).Path
if (-not [string]::IsNullOrWhiteSpace($CapturePath)) {
    $env:NIKAMI_AURORA_CAPTURE = [IO.Path]::GetFullPath($CapturePath)
    $env:NIKAMI_AURORA_CAPTURE_FRAME = $CaptureFrame.ToString(
        [Globalization.CultureInfo]::InvariantCulture)
}
if (-not [string]::IsNullOrWhiteSpace($CaptureDialogueNode)) {
    $env:NIKAMI_AURORA_CAPTURE_DIALOGUE_NODE = $CaptureDialogueNode
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
if ($LipSyncCloseup) {
    $env:NIKAMI_AURORA_CAPTURE_LIP_CLOSEUP = "1"
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
    $env:NIKAMI_AURORA_TEST_OPEN_LOCKER = "1"
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
    $env:NIKAMI_AURORA_TEST_INVENTORY_SCROLL_REPEAT = "3"
}
if ($EquipmentScreen) {
    if ($OpenXR -or $OpenXRSimulator) {
        throw "-EquipmentScreen is a flat presentation gate."
    }
    $env:NIKAMI_AURORA_TEST_EQUIPMENT_SCREEN = "1"
    $env:NIKAMI_AURORA_TEST_OPEN_LOCKER = "1"
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
    $env:NIKAMI_AURORA_FLAT_UI_REFERENCE_VIEWPORT = "800x600"
}

try {
    & dotnet build (Join-Path $repo "godot\Nikami.Aurora.Godot.csproj") --configuration Debug
    if ($LASTEXITCODE -ne 0) {
        throw "Godot C# build failed with code $LASTEXITCODE"
    }
    $godotArguments = @("--path", (Join-Path $repo "godot"))
    if ($LoadingScreenCapture -or $HudScreen -or $InventoryScreen -or $EquipmentScreen) {
        # mipc8x6 is KOTOR's owned 800x600 HUD contract.  Keeping flat
        # acceptance captures at that exact viewport prevents widescreen
        # stretching from being mistaken for source-layout drift.
        $godotArguments += @("--windowed", "--resolution", "800x600")
    }
    if ($OpenXR) {
        $godotArguments += @("--xr-mode", "on")
    }
    if ($OpenXRSimulator) {
        $godotArguments += @("--rendering-method", "mobile")
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
    $godotProcess.WaitForExit()
    if ($redirectGodotOutput) {
        [IO.File]::WriteAllText(
            $resolvedGodotStdoutPath, $stdoutTask.GetAwaiter().GetResult())
        [IO.File]::WriteAllText(
            $resolvedGodotStderrPath, $stderrTask.GetAwaiter().GetResult())
    }
    if ($godotProcess.ExitCode -ne 0) {
        throw "Godot exited with code $($godotProcess.ExitCode)"
    }
}
finally {
    Remove-Item Env:NIKAMI_AURORA_MODULE_MANIFEST -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE_FRAME -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE_DIALOGUE_NODE -ErrorAction SilentlyContinue
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
    Remove-Item Env:NIKAMI_AURORA_SHOWCASE_ROUTE -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_SHOWCASE_EXIT_ON_COMPLETE -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_SKIP_OPENING_DIALOGUE -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_OPENXR -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_OPENXR_EXPECT_ACTIVE -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_XR_SPECTATOR -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE_CLEAN -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE_LIP_CLOSEUP -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE_PLAYER_EQUIPMENT_CLOSEUP -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE_CHAIR_CLOSEUP -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE_XR_BODY_LOOKDOWN -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE_LOADING_SCREEN -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_INVENTORY_SCREEN -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_INVENTORY_QUEST_FILTER -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_INVENTORY_SCROLL_REPEAT -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_EQUIPMENT_SCREEN -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_EQUIPMENT_MENU_TRANSACTION -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_TEST_FLAT_MENU_NAVIGATION -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_FLAT_UI_REFERENCE_VIEWPORT -ErrorAction SilentlyContinue
    if ($hadXrRuntimeJson) {
        $env:XR_RUNTIME_JSON = $previousXrRuntimeJson
    }
    else {
        Remove-Item Env:XR_RUNTIME_JSON -ErrorAction SilentlyContinue
    }
}
