$settingsPath = Join-Path $env:LOCALAPPDATA "ColonyOptimizer\settings.json"

if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
    exit 0
}

try {
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
}
catch {
    exit 0
}

$recordedPlans = @($settings.LastPlanPath) + @($settings.RecentPlans) |
    Where-Object { $_ -and [System.IO.Path]::GetExtension([string]$_).Equals(".colonyplan", [System.StringComparison]::OrdinalIgnoreCase) } |
    Sort-Object -Unique

foreach ($planPath in $recordedPlans) {
    Remove-Item -LiteralPath $planPath -Force -ErrorAction SilentlyContinue
}
