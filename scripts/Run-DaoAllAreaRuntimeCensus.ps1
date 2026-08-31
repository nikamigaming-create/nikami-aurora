[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RuntimeCatalog,
    [Parameter(Mandatory = $true)]
    [string]$AreaMatrix,
    [Parameter(Mandatory = $true)]
    [string]$OutputRoot,
    [string]$GodotConsolePath = 'Godot_v4.6.3-stable_mono_win64_console.exe',
    [int]$TimeoutSeconds = 180,
    [int]$StartIndex = 0,
    [int]$MaxAreas = 0,
    [string]$AreaId = '',
    [switch]$SkipBuild,
    [switch]$Rerun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Read-Json([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "DAO runtime census input is absent: $Path"
    }
    Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Marker([string]$Log, [string]$Name) {
    $match = [regex]::Match($Log, "(?m)^.*$([regex]::Escape($Name))[^\r\n]*$")
    if ($match.Success) { return $match.Value.Trim() }
    return ''
}

function Token([string]$Marker, [string]$Name) {
    $match = [regex]::Match($Marker, "(?:^| )$([regex]::Escape($Name))=(?<value>[^ ]+)")
    if ($match.Success) { return $match.Groups['value'].Value }
    return ''
}

function Write-Report([string]$Path, [object[]]$Rows, [string]$CatalogHash,
    [string]$MatrixHash) {
    $report = [pscustomobject]@{
        schema = 'opendao-all-area-runtime-census-v1'
        updatedAtUtc = [DateTime]::UtcNow.ToString('O',
            [Globalization.CultureInfo]::InvariantCulture)
        runtimeCatalogSha256 = $CatalogHash
        areaMatrixSha256 = $MatrixHash
        expectedAreas = 352
        attemptedAreas = $Rows.Count
        runtimeLoadPassed = @($Rows | Where-Object runtimeLoadStatus -eq 'pass').Count
        strictPbrPassed = @($Rows | Where-Object strictPbrStatus -eq 'pass').Count
        lightingPassed = @($Rows | Where-Object lightingStatus -eq 'pass').Count
        effectsPassed = @($Rows | Where-Object effectsStatus -eq 'pass').Count
        cameraSpawnVisibilityPassed = @($Rows |
            Where-Object cameraSpawnVisibilityStatus -eq 'pass').Count
        rows = @($Rows | Sort-Object sourceKey)
    }
    [IO.File]::WriteAllText($Path, ($report | ConvertTo-Json -Depth 12),
        [Text.UTF8Encoding]::new($false))
}

if ($TimeoutSeconds -lt 10) { throw 'DAO runtime census timeout must be at least ten seconds.' }
$catalogPath = [IO.Path]::GetFullPath($RuntimeCatalog)
$matrixPath = [IO.Path]::GetFullPath($AreaMatrix)
$outputPath = [IO.Path]::GetFullPath($OutputRoot)
$repository = Split-Path -Parent $PSScriptRoot
$godotProject = Join-Path $repository 'godot'
$catalog = Read-Json $catalogPath
$matrix = Read-Json $matrixPath
if ([string]$matrix.schema -ne 'opendao-all-level-render-matrix-v1' -or
    @($matrix.levels).Count -ne 352) {
    throw 'DAO runtime census requires the complete 352-row all-level matrix.'
}
$consoleCommand = Get-Command $GodotConsolePath -ErrorAction Stop
$consolePath = [IO.Path]::GetFullPath($consoleCommand.Source)
$godotProcessPath = $consolePath
if ($consolePath.EndsWith('_console.exe', [StringComparison]::OrdinalIgnoreCase)) {
    $directCandidate = $consolePath.Substring(
        0, $consolePath.Length - '_console.exe'.Length) + '.exe'
    if (Test-Path -LiteralPath $directCandidate -PathType Leaf) {
        $godotProcessPath = $directCandidate
    }
}
[void][IO.Directory]::CreateDirectory($outputPath)
$reportPath = Join-Path $outputPath 'dao-all-area-runtime-census-v1.json'
$characterPath = Join-Path $outputPath 'runtime-census-character.json'
[IO.File]::WriteAllText($characterPath, (@{
    schema = 'opendao-character-v1'
    name = 'Runtime Census'
    origin = 'human-noble'
    race = 'human'
    gender = 'female'
    class = 'warrior'
    appearance = 'preset-1'
} | ConvertTo-Json), [Text.UTF8Encoding]::new($false))

if (-not $SkipBuild) {
    & dotnet build (Join-Path $godotProject 'Nikami.Aurora.Godot.csproj') `
        --configuration Debug
    if ($LASTEXITCODE -ne 0) { throw 'DAO runtime census Godot build failed.' }
}

$catalogHash = (Get-FileHash -LiteralPath $catalogPath -Algorithm SHA256).Hash.ToLowerInvariant()
$matrixHash = (Get-FileHash -LiteralPath $matrixPath -Algorithm SHA256).Hash.ToLowerInvariant()
$existingRows = @()
if (Test-Path -LiteralPath $reportPath -PathType Leaf) {
    $existing = Read-Json $reportPath
    if ([string]$existing.runtimeCatalogSha256 -ne $catalogHash -or
        [string]$existing.areaMatrixSha256 -ne $matrixHash) {
        throw 'DAO runtime census cannot resume against a different catalog or matrix.'
    }
    $existingRows = @($existing.rows)
}
$rowsByKey = @{}
foreach ($row in $existingRows) { $rowsByKey[[string]$row.sourceKey] = $row }
$matrixByKey = @{}
foreach ($row in @($matrix.levels)) { $matrixByKey[[string]$row.sourceKey] = $row }

$areas = @($catalog.areas | Where-Object { [bool]$_.ready } | Sort-Object key)
if (-not [string]::IsNullOrWhiteSpace($AreaId)) {
    $areas = @($areas | Where-Object { [string]$_.id -eq $AreaId })
    if ($areas.Count -eq 0) { throw "DAO runtime census area is absent: $AreaId" }
}
elseif ($StartIndex -gt 0) {
    $areas = @($areas | Select-Object -Skip $StartIndex)
}
if ($MaxAreas -gt 0) { $areas = @($areas | Select-Object -First $MaxAreas) }

$clearVariables = @(
    'OPENDAO_AREA_RUNTIME_EVIDENCE_ROOT', 'OPENDAO_CHARACTER_CREATION_ACCEPTANCE',
    'OPENDAO_CITY_ELF_PLAYABLE_SMOKE', 'OPENDAO_GAME_START_CAPTURE',
    'OPENDAO_LOCOMOTION_CAPTURE', 'OPENDAO_EFFECT_CLOSE_CAPTURE', 'DAOPEN_CAPTURE',
    'OPENDAO_LOCOMOTION_TEST', 'OPENDAO_LOADING_CAPTURE', 'OPENDAO_MAIN_MENU_CAPTURE',
    'OPENDAO_PLAYABLE_DESTINATION_CAPTURE', 'OPENDAO_CITY_ELF_SKY_CAPTURE'
)

foreach ($area in $areas) {
    $sourceKey = [string]$area.key
    if (-not $Rerun -and $rowsByKey.ContainsKey($sourceKey)) { continue }
    $matrixRow = $matrixByKey[$sourceKey]
    if ($null -eq $matrixRow) { throw "DAO matrix omitted source key: $sourceKey" }
    $safeId = ([string]$area.id -replace '[^A-Za-z0-9_.-]', '_')
    $areaOutput = Join-Path $outputPath ("{0}-{1}" -f $safeId,
        ([string]$matrixRow.profileSha256).Substring(0, 12))
    [void][IO.Directory]::CreateDirectory($areaOutput)
    $logPath = Join-Path $areaOutput 'runtime.log'
    $consoleOutputPath = Join-Path $areaOutput 'console.log'
    foreach ($stalePath in @($logPath, $consoleOutputPath)) {
        if (Test-Path -LiteralPath $stalePath -PathType Leaf) {
            [IO.File]::Delete($stalePath)
        }
    }

    $start = [DateTime]::UtcNow
    $processInfo = [Diagnostics.ProcessStartInfo]::new()
    $processInfo.FileName = $godotProcessPath
    $processInfo.WorkingDirectory = $repository
    $processInfo.UseShellExecute = $false
    $processInfo.CreateNoWindow = $true
    $processInfo.RedirectStandardOutput = $true
    $processInfo.RedirectStandardError = $true
    foreach ($argument in @('--headless', '--path', $godotProject, '--rendering-method',
            'forward_plus', '--log-file', $logPath, '--quit-after', '10800')) {
        [void]$processInfo.ArgumentList.Add($argument)
    }
    foreach ($name in $clearVariables) { $processInfo.Environment[$name] = '' }
    $stateRoot = Join-Path $areaOutput 'state'
    [void][IO.Directory]::CreateDirectory($stateRoot)
    $settings = @{
        OPENDAO_PROFILE = [IO.Path]::GetFullPath([string]$area.profilePath)
        OPENDAO_SELECTED_PROFILE = [IO.Path]::GetFullPath([string]$area.profilePath)
        OPENDAO_CHARACTER_PROFILE = $characterPath
        OPENDAO_PLAYER_SESSION = Join-Path $stateRoot 'player-session.json'
        OPENDAO_PENDING_TRANSITION = Join-Path $stateRoot 'pending-transition.json'
        DAOPEN_STORY_STATE = Join-Path $stateRoot 'story-state.json'
        OPENDAO_CATALOG = $catalogPath
        OPENDAO_CSHARP_WORLD_SMOKE_EXIT = '1'
        OPENDAO_TEST_NO_PERSIST = '1'
        NIKAMI_AURORA_PROFILE = 'dragon-age-origins'
        NIKAMI_AURORA_PRESENTATION_TIER = 'enhanced'
        NIKAMI_AURORA_DAO_CACHE_ROOT = [IO.Path]::GetDirectoryName($catalogPath)
        NIKAMI_AURORA_DAO_GENERATED_ROOT = Join-Path ([IO.Path]::GetDirectoryName($catalogPath)) 'generated'
        DRAGON_AGE_GODOT_GAME_ROOT = [IO.Path]::GetFullPath([string]$catalog.gameRoot)
    }
    foreach ($entry in $settings.GetEnumerator()) {
        $processInfo.Environment[$entry.Key] = [string]$entry.Value
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $processInfo
    [void]$process.Start()
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
    if ($timedOut) {
        $process.Kill($true)
        $process.WaitForExit()
    }
    [Threading.Tasks.Task]::WaitAll(@($stdout, $stderr))
    [IO.File]::WriteAllText($consoleOutputPath,
        $stdout.Result + [Environment]::NewLine + $stderr.Result,
        [Text.UTF8Encoding]::new($false))
    $log = if (Test-Path -LiteralPath $logPath -PathType Leaf) {
        Get-Content -LiteralPath $logPath -Raw
    } else { '' }
    $worldReady = Marker $log 'OPENDAO_WORLD_READY'
    $smoke = Marker $log 'OPENDAO_CSHARP_WORLD_SMOKE_PASS'
    $materials = Marker $log 'OPENDAO_WORLD_MATERIAL_CENSUS'
    $effects = Marker $log 'OPENDAO_WORLD_EFFECT_CENSUS'
    $atmosphere = Marker $log 'OPENDAO_AUTHORED_ATMOSPHERE'
    $lighting = Marker $log 'OPENDAO_AUTHORED_LIGHTING'
    $surfaceCount = [long](Token $materials 'surfaces')
    $pbrCount = [long](Token $materials 'pbr_contract_ready')
    $strictPbr = if ((Token $materials 'binding_status') -eq 'ready' -and
        (Token $materials 'identity_status') -eq 'ready' -and
        $surfaceCount -gt 0 -and $pbrCount -eq $surfaceCount) { 'pass' } else { 'fail' }
    $runtimeLoad = if (-not $timedOut -and $process.ExitCode -eq 0 -and
        $worldReady.Length -gt 0 -and $smoke.Length -gt 0) { 'pass' } else { 'fail' }
    $lightingStatus = if ($atmosphere -match ' status=ready ' -and
        $lighting -match ' status=ready ') { 'pass' } else { 'unsupported' }
    $effectStatus = if ($effects.Length -eq 0) { 'fail' }
        elseif ((Token $effects 'status') -eq 'ready') { 'pass' } else { 'partial' }
    $spawnWarning = $log -match 'Player spawn has no walkable surface'
    $cameraStatus = if ($runtimeLoad -eq 'pass' -and -not $spawnWarning) {
        'prerequisite-pass'
    } else { 'unverified' }
    $ended = [DateTime]::UtcNow
    $row = [pscustomobject]@{
        sourceKey = $sourceKey
        areaId = [string]$area.id
        layout = [string]$area.layout
        profileSha256 = [string]$matrixRow.profileSha256
        worldManifestSha256 = [string]$matrixRow.worldManifestSha256
        startedAtUtc = $start.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
        endedAtUtc = $ended.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
        durationMilliseconds = [long]($ended - $start).TotalMilliseconds
        timedOut = $timedOut
        exitCode = if ($timedOut) { $null } else { $process.ExitCode }
        runtimeLoadStatus = $runtimeLoad
        strictPbrStatus = $strictPbr
        lightingStatus = $lightingStatus
        effectsStatus = $effectStatus
        cameraSpawnVisibilityStatus = $cameraStatus
        cameraSpawnVisibilityProof = 'runtime-prerequisite-only-no-visual-los-proof'
        expectedActiveActors = [int]$matrixRow.creatureGallery.expected
        logPath = $logPath
        logSha256 = if (Test-Path -LiteralPath $logPath -PathType Leaf) {
            (Get-FileHash -LiteralPath $logPath -Algorithm SHA256).Hash.ToLowerInvariant()
        } else { $null }
        markers = [pscustomobject]@{
            worldReady = $worldReady
            smoke = $smoke
            materials = $materials
            effects = $effects
            atmosphere = $atmosphere
            lighting = $lighting
        }
    }
    $rowsByKey[$sourceKey] = $row
    $allRows = @($rowsByKey.Values)
    Write-Report $reportPath $allRows $catalogHash $matrixHash
    Write-Output (("OPENDAO_AREA_RUNTIME_CENSUS area={0} layout={1} exit={2} " +
        "load={3} pbr={4} lighting={5} effects={6} camera={7} rows={8}/352") -f
        $row.areaId, $row.layout, $row.exitCode, $runtimeLoad, $strictPbr,
        $lightingStatus, $effectStatus, $cameraStatus, $allRows.Count)
}

Write-Report $reportPath @($rowsByKey.Values) $catalogHash $matrixHash
