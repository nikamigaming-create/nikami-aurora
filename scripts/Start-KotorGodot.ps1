[CmdletBinding()]
param(
    [string]$Manifest,
    [string]$Godot,
    [string]$CapturePath,
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
    [switch]$SkipOpeningDialogue,
    [switch]$OpenXR,
    [switch]$CleanCapture,
    [switch]$LipSyncCloseup,
    [switch]$EquipmentCloseup,
    [switch]$ChairCloseup,
    [switch]$CaptureAndExit
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Manifest)) {
    $Manifest = Join-Path $repo "local\kotor\end_m01aa\module-manifest.json"
}
if (-not (Test-Path -LiteralPath $Manifest -PathType Leaf)) {
    throw "Module manifest not found: $Manifest. Run scripts/Import-KotorModule.ps1 first."
}
if ([string]::IsNullOrWhiteSpace($Godot)) {
    $command = Get-Command "Godot_v4.6.3-stable_mono_win64.exe" -ErrorAction SilentlyContinue
    if (-not $command) {
        throw "Godot 4.6.3 .NET was not found on PATH. Pass -Godot explicitly."
    }
    $Godot = $command.Source
}

$env:NIKAMI_AURORA_MODULE_MANIFEST = (Resolve-Path -LiteralPath $Manifest).Path
if (-not [string]::IsNullOrWhiteSpace($CapturePath)) {
    $env:NIKAMI_AURORA_CAPTURE = [IO.Path]::GetFullPath($CapturePath)
    $env:NIKAMI_AURORA_CAPTURE_FRAME = $CaptureFrame.ToString(
        [Globalization.CultureInfo]::InvariantCulture)
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
if (-not [string]::IsNullOrWhiteSpace($TestPlayerAnimation)) {
    $env:NIKAMI_AURORA_TEST_PLAYER_ANIMATION = $TestPlayerAnimation
}
if ($SkipOpeningDialogue) {
    $env:NIKAMI_AURORA_SKIP_OPENING_DIALOGUE = "1"
}
if ($OpenXR) {
    $env:NIKAMI_AURORA_OPENXR = "1"
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

try {
    & dotnet build (Join-Path $repo "godot\Nikami.Aurora.Godot.csproj") --configuration Debug
    if ($LASTEXITCODE -ne 0) {
        throw "Godot C# build failed with code $LASTEXITCODE"
    }
    $godotArguments = @("--path", (Join-Path $repo "godot"))
    if ($OpenXR) {
        $godotArguments += @("--xr-mode", "on")
    }
    & $Godot @godotArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Godot exited with code $LASTEXITCODE"
    }
}
finally {
    Remove-Item Env:NIKAMI_AURORA_MODULE_MANIFEST -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE_FRAME -ErrorAction SilentlyContinue
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
    Remove-Item Env:NIKAMI_AURORA_SKIP_OPENING_DIALOGUE -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_OPENXR -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE_CLEAN -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE_LIP_CLOSEUP -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE_PLAYER_EQUIPMENT_CLOSEUP -ErrorAction SilentlyContinue
    Remove-Item Env:NIKAMI_AURORA_CAPTURE_CHAIR_CLOSEUP -ErrorAction SilentlyContinue
}
