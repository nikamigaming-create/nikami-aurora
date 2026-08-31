[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path -Parent $PSScriptRoot

$effect = Get-Content -LiteralPath (Join-Path $repository 'godot/csharp/Infrastructure/World/DaoSourceEffectMaterializer.cs') -Raw
$water = Get-Content -LiteralPath (Join-Path $repository 'godot/csharp/Infrastructure/World/DaoWaterMaterialFactory.cs') -Raw
$world = Get-Content -LiteralPath (Join-Path $repository 'godot/csharp/Infrastructure/World/GodotWorldContentLoader.cs') -Raw
$materials = Get-Content -LiteralPath (Join-Path $repository 'godot/csharp/Infrastructure/World/DaoCharacterMaterialPostprocessor.cs') -Raw
$terrain = Get-Content -LiteralPath (Join-Path $repository 'godot/csharp/Infrastructure/World/DaoTerrainMaterialFactory.cs') -Raw
foreach ($source in @($effect, $water)) {
    if ($source -notmatch 'RenderingQualityPolicy\.ParseTier' -or
        $source -notmatch 'RenderingQualityPolicy\.ParseBackend') {
        throw 'DAO enhanced presentation bypasses the Core tier/backend parser.'
    }
}
if ($effect -notmatch 'ProximityFadeEnabled = readability\.ProximityFadeDistanceMeters\.HasValue' -or
    $effect -notmatch 'DragonAgeOriginsEffectPresentationPolicy\.Evaluate' -or
    $effect -notmatch 'DistanceFadeMode = BaseMaterial3D\.DistanceFadeModeEnum\.Disabled' -or
    $effect -match 'DistanceFadeMaxDistance = enhancedPresentation' -or
    $effect -notmatch 'source\.Blend == DragonAgeEffectBlend\.Additive' -or
    $effect -notmatch 'EnhancedAtlasTexture' -or
    $effect -notmatch 'EnhancedFireScale\(source\.Name\)' -or
    $effect -notmatch 'EnhancedFireTint\(source\.Name\)' -or
    $effect -notmatch 'new Color\(\.52f, \.24f, \.07f, \.92f\)') {
    throw 'DAO supported effects lack enhanced-only soft-depth/exposure handling.'
}
if ($water -notmatch '!descriptor\.RenderAuthorized && !enhancedPresentation' -or
    $water -notmatch 'opaque_white_fallback=blocked' -or
    $water -notmatch 'source_semantics=') {
    throw 'DAO water tier boundary or enhanced non-parity telemetry is missing.'
}
if ($world -notmatch 'RequirePbrContract\(identity\)' -or
    $world -notmatch 'RequirePbrCoverage' -or
    $materials -notmatch 'pbrMetallicRoughness must be an object' -or
    $materials -notmatch 'must contain a texture index') {
    throw 'DAO global visible-material PBR identity gate is incomplete.'
}
if ($terrain -notmatch 'source_palette_identity_sha256=' -or
    $water -notmatch 'source_normal_identity_sha256=' -or
    $terrain -match 'source_palette=\{SourceIdentity' -or
    $water -match 'source_normal=\{SourceIdentity') {
    throw 'DAO derived material identity embeds an ambiguous nested token stream.'
}
$legacyFallback = Join-Path $repository 'godot/shaders/dao_fallback_fire.gdshader'
if (Test-Path -LiteralPath $legacyFallback) {
    throw 'Legacy procedural fallback fire shader must not remain available.'
}

Write-Output 'OPENDAO_ENHANCED_PRESENTATION_TIERS status=pass source_water=fail-closed enhanced_water=bounded-pbr visible_pbr=exact-all-surfaces material_identity_join=sha256 legacy_fallback_fire=absent source_particles=unchanged enhanced_particles=proximity-softening-atlas-feather-bounded-exposure distance_fade=blocked tier_parser=core parity_claim=none'
