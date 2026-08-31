[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = Split-Path -Parent $PSScriptRoot
$pairs = @(
    @('dao_character_armour_skin.gdshader', 'dao_character_armour_skin_enhanced.gdshader'),
    @('dao_character_eyelash.gdshader', 'dao_character_eyelash_enhanced.gdshader'),
    @('dao_facefx_material.gdshader', 'dao_facefx_material_enhanced.gdshader'),
    @('dao_character_hair.gdshader', 'dao_character_hair_enhanced.gdshader')
)

foreach ($pair in $pairs) {
    $sourcePath = Join-Path $repository "godot/shaders/$($pair[0])"
    $enhancedPath = Join-Path $repository "godot/shaders/$($pair[1])"
    $source = Get-Content -LiteralPath $sourcePath -Raw
    $enhanced = Get-Content -LiteralPath $enhancedPath -Raw
    if ($source -notmatch 'render_mode[^;]*\bunshaded\b') {
        throw "DAO source character shader lost authored unshaded mode: $($pair[0])"
    }
    if ($enhanced -match 'render_mode[^;]*\bunshaded\b') {
        throw "DAO enhanced character shader cannot enter the PBR light path: $($pair[1])"
    }
    foreach ($output in 'ALBEDO', 'ROUGHNESS', 'SPECULAR') {
        if ($enhanced -notmatch "\b$output\s*=") {
            throw "DAO enhanced character shader does not write $output`: $($pair[1])"
        }
    }
    if ($enhanced -notmatch 'filter_linear_mipmap_anisotropic') {
        throw "DAO enhanced character shader lacks anisotropic source sampling: $($pair[1])"
    }
}

$postprocessorPath = Join-Path $repository 'godot/csharp/Infrastructure/World/DaoCharacterMaterialPostprocessor.cs'
$postprocessor = Get-Content -LiteralPath $postprocessorPath -Raw
if ($postprocessor -notmatch 'OPENDAO_CHARACTER_PBR_PIPELINE' -or
    $postprocessor -notmatch 'RenderingQualityPolicy\.ParseTier') {
    throw 'DAO character shader tier selection bypasses Core policy or lacks telemetry.'
}

Write-Output 'OPENDAO_CHARACTER_SHADER_TIERS status=pass source=authored-unshaded enhanced=godot-pbr variants=4 tier_parser=core anisotropic=ready masks=preserved'
