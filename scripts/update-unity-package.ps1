param(
    [Parameter(Mandatory = $true)][string]$ProjectPath,
    [string]$PackagePath = ''
)
$ErrorActionPreference = 'Stop'
if (-not $PackagePath) { $PackagePath = Join-Path $PSScriptRoot '../Packages/io.github.kokokaiyokan.bamu' }
$project = (Resolve-Path -LiteralPath $ProjectPath).Path.TrimEnd('\', '/')
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$manifest = Join-Path $project 'Packages/manifest.json'
$resolvedManifest = (Resolve-Path -LiteralPath $manifest).Path
if (-not $resolvedManifest.StartsWith($project + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Manifest must be inside the specified Unity project.'
}
$packageInfo = Get-Content -LiteralPath (Join-Path $package 'package.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($packageInfo.name -ne 'io.github.kokokaiyokan.bamu') { throw 'Unexpected package identifier.' }
$before = [IO.File]::ReadAllText($resolvedManifest)
$data = $before | ConvertFrom-Json
if (-not $data.dependencies.'pm.booth.unity-bridge' -and -not $data.dependencies.'io.github.kokokaiyokan.bamu') {
    throw 'The old package dependency was not found. Install the new package with Package Manager.'
}
if ($data.dependencies.'pm.booth.unity-bridge' -and $data.dependencies.'io.github.kokokaiyokan.bamu') { throw 'Both package dependencies exist; resolve the duplicate before updating.' }
$newReference = 'file:' + $package.Replace('\', '/')
if ($data.dependencies.'io.github.kokokaiyokan.bamu' -eq $newReference) { Write-Output 'BAMU dependency is already configured.'; return }
$isPathUpdate = [bool]$data.dependencies.'io.github.kokokaiyokan.bamu'
$replacement = '"io.github.kokokaiyokan.bamu": ' + (ConvertTo-Json -InputObject ('file:' + $package.Replace('\', '/')) -Compress)
$pattern = if ($isPathUpdate) { '"io\.github\.kokokaiyokan\.bamu"\s*:\s*"[^"]*"' } else { '"pm\.booth\.unity-bridge"\s*:\s*"[^"]*"' }
$after = [regex]::Replace($before, $pattern, [Text.RegularExpressions.MatchEvaluator]{ param($match) $replacement })
$null = $after | ConvertFrom-Json
if ($after -eq $before) { throw 'No dependency was replaced.' }
$backup = $resolvedManifest + $(if ($isPathUpdate) { '.pre-bamu-path.bak' } else { '.pre-bamu.bak' })
if (-not (Test-Path -LiteralPath $backup)) { [IO.File]::WriteAllText($backup, $before, [Text.UTF8Encoding]::new($false)) }
$temporary = $resolvedManifest + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
$swapBackup = $temporary + '.bak'
try {
    [IO.File]::WriteAllText($temporary, $after, [Text.UTF8Encoding]::new($false))
    if ([IO.File]::ReadAllText($resolvedManifest) -ne $before) { throw 'Manifest changed during update; no replacement was made.' }
    [IO.File]::Replace($temporary, $resolvedManifest, $swapBackup)
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    if (Test-Path -LiteralPath $swapBackup) { Remove-Item -LiteralPath $swapBackup }
}
Write-Output ('Updated the local BAMU dependency. Other dependencies were preserved; backup: ' + [IO.Path]::GetFileName($backup))
