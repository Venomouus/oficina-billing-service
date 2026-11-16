$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'BillingService.Tests.csproj'
$results = Join-Path $PSScriptRoot ('TestResults/coverage-' + [Guid]::NewGuid().ToString('N'))

dotnet test $project --configuration Release --collect 'XPlat Code Coverage' --results-directory $results -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$reports = @(Get-ChildItem -LiteralPath $results -Directory | ForEach-Object {
    Get-ChildItem -LiteralPath $_.FullName -File -Filter 'coverage.cobertura.xml'
})
if ($reports.Count -ne 1) { throw "Expected one Cobertura report, found $($reports.Count)." }
[xml]$coverage = Get-Content -LiteralPath $reports[0].FullName
$packages = @($coverage.coverage.packages.package)
if ($packages.Count -ne 1 -or $packages[0].name -ne 'BillingService') {
    throw 'Coverage report must contain the entire BillingService production assembly only.'
}
$covered = [int]$coverage.coverage.'lines-covered'
$valid = [int]$coverage.coverage.'lines-valid'
if ($valid -le 0) { throw 'Coverage report has no production lines.' }
$percent = 100.0 * $covered / $valid
Write-Host ('BillingService line coverage: {0}/{1} ({2:N2}%)' -f $covered, $valid, $percent)
Write-Host "Cobertura report: $($reports[0].FullName)"
if ($percent -lt 80) { throw 'BillingService line coverage is below 80%.' }
