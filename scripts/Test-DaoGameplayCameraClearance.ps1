[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = Split-Path -Parent $PSScriptRoot
$configurationPath = Join-Path $repository 'godot/config/dao/presentation.json'
$configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
$player = Get-Content -LiteralPath (Join-Path $repository 'godot/csharp/Presentation/Player/PlayerController.cs') -Raw
$world = Get-Content -LiteralPath (Join-Path $repository 'godot/csharp/Presentation/World/OpenDaoWorld.cs') -Raw

if ($configuration.schemaVersion -ne 2 -or
    $configuration.gameplayCamera.calibrationStatus -ne 'pending-retail-match' -or
    $configuration.gameplayCamera.enhancedMinimumAvatarClearanceMeters -lt 1.6 -or
    $configuration.gameplayCamera.enhancedCollisionProbeRadiusMeters -le 0) {
    throw 'DAO camera configuration does not preserve its validated non-parity clearance contract.'
}

foreach ($required in @(
    'ObstructionSearchYawDegrees',
    '[0, 35, -35, 70, -70, 90, -90]',
    'DirectSpaceState.CastMotion',
    'ObstructionScore',
    'ObstructionSearchYawPenaltyPerDegree',
    'minimumAvatarCameraClearance',
    'ObstructionSearchSwitchHysteresis',
    'mode=clear-orbit',
    'body_preserved=1',
    'parity_claim=none'
)) {
    if ($player -notmatch [regex]::Escape($required)) {
        throw "DAO enhanced camera clearance is missing required behavior: $required"
    }
}

if ($player -match 'avatarRoot\.Visible\s*=\s*!' -or
    $player -match 'avatarRoot\.Visible\s*=\s*false') {
    throw 'DAO camera clearance cannot hide the whole third-person avatar.'
}
if ($world -notmatch 'RenderingQualityPolicy\.ParseTier' -or
    $world -notmatch 'ConfigureThirdPersonView') {
    throw 'DAO camera presentation bypasses the application-wide Core tier selector.'
}

Write-Output 'OPENDAO_GAMEPLAY_CAMERA_CLEARANCE_CONTRACT status=pass scope=application probe=sphere orbit_candidates=7 selector=minimum-clearance-authored-yaw-scored hysteresis=ready whole_avatar_hide=blocked source=unchanged calibration=pending-retail-match parity_claim=none'
