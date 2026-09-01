[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$GameRoot = $env:NIKAMI_KOTOR_ROOT,

    [ValidateSet('kotor', 'kotor2')]
    [string]$Profile = 'kotor',

    [ValidatePattern('^[A-Za-z0-9_]{1,16}$')]
    [string]$Module = "end_m01aa",

    [string]$OutputRoot,

    [string]$MdlOps,

    [string]$RuntimeConfig,

    [ValidateRange(0, 65535)]
    [int]$PlayerAppearanceId = 137,

    [ValidateRange(0, 65535)]
    [int]$PlayerPortraitId = 18,

    [ValidateRange(0, 255)]
    [int]$PlayerClassId = 0,

    [string]$Python = "py"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$Module = $Module.ToLowerInvariant()
if ([string]::IsNullOrWhiteSpace($GameRoot)) {
    throw "Pass -GameRoot or set NIKAMI_KOTOR_ROOT."
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repo "local\$Profile\$Module"
}
if ([string]::IsNullOrWhiteSpace($MdlOps)) {
    $MdlOps = Join-Path $repo "local\tools\mdlops\mdlops.exe"
}
if ([string]::IsNullOrWhiteSpace($RuntimeConfig)) {
    $RuntimeConfig = Join-Path $repo "config\kotor-runtime.json"
}
if (-not (Test-Path -LiteralPath $MdlOps -PathType Leaf)) {
    throw "MDLOps not found: $MdlOps. Run scripts/Bootstrap-MDLOps.ps1 first."
}
if (-not (Test-Path -LiteralPath $RuntimeConfig -PathType Leaf)) {
    throw "KOTOR runtime configuration not found: $RuntimeConfig"
}

$arguments = @()
if ((Split-Path -Leaf $Python) -ieq "py" -or (Split-Path -Leaf $Python) -ieq "py.exe") {
    $arguments += "-3.12"
}
$arguments += @(
    (Join-Path $PSScriptRoot "import_kotor_module.py"),
    "--game-root", $GameRoot,
    "--profile", $Profile,
    "--module", $Module,
    "--output", $OutputRoot,
    "--mdlops", $MdlOps,
    "--runtime-config", $RuntimeConfig,
    "--player-appearance-id", $PlayerAppearanceId,
    "--player-portrait-id", $PlayerPortraitId,
    "--player-class-id", $PlayerClassId
)

& $Python @arguments
if ($LASTEXITCODE -ne 0) {
    throw "KOTOR module import failed with exit code $LASTEXITCODE"
}
