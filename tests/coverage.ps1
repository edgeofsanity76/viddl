# Runs the unit tests with coverage and prints a per-library summary.
# Usage (from the repository root):  powershell -ExecutionPolicy Bypass -File tests/coverage.ps1 [-Configuration Release]
# On GitHub Actions the summary is also added to the workflow run's summary page.
param(
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$results = Join-Path $PSScriptRoot 'TestResults'
if (Test-Path $results) { Remove-Item $results -Recurse -Force }

$summary = @('| Library | Tests | Lines | Branches |', '|---|---|---|---|')
$projects = @{ 'viddl.Core' = 'viddl.Core.Tests'; 'viddl.Downloads' = 'viddl.Downloads.Tests' }
foreach ($lib in $projects.Keys | Sort-Object) {
    $testProject = Join-Path $PSScriptRoot $projects[$lib]
    $out = Join-Path $results $lib
    $testOutput = dotnet test $testProject -c $Configuration --collect:"XPlat Code Coverage" --logger "trx;LogFileName=$lib.trx" --results-directory $out
    $exit = $LASTEXITCODE
    $testOutput | Select-String 'Passed!|Failed!|error' | ForEach-Object { $_.Line }
    if ($exit -ne 0) { throw "Tests failed for $lib" }

    $report = Get-ChildItem $out -Recurse -Filter coverage.cobertura.xml | Select-Object -First 1
    [xml]$xml = Get-Content $report.FullName
    $package = $xml.coverage.packages.package | Where-Object name -eq $lib
    $lines = @($package.classes.class.lines.line)
    $covered = @($lines | Where-Object hits -ne '0').Count
    $lineRate = [double]$package.'line-rate'
    $branchRate = [double]$package.'branch-rate'
    $tests = ($testOutput | Select-String 'Total:\s*(\d+)').Matches | Select-Object -Last 1 | ForEach-Object { $_.Groups[1].Value }

    '{0,-16} lines {1,6:P1} ({2}/{3})   branches {4,6:P1}' -f $lib, $lineRate, $covered, $lines.Count, $branchRate
    $summary += '| {0} | {1} | {2:P1} ({3}/{4}) | {5:P1} |' -f $lib, $tests, $lineRate, $covered, $lines.Count, $branchRate
    foreach ($class in $package.classes.class) {
        $missed = @($class.lines.line | Where-Object hits -eq '0' | ForEach-Object number | Sort-Object { [int]$_ } -Unique)
        if ($missed.Count) { '    not covered: {0} line {1}' -f $class.filename, ($missed -join ',') }
    }
}

if ($env:GITHUB_STEP_SUMMARY) {
    @('## Test coverage', '') + $summary | Add-Content -Path $env:GITHUB_STEP_SUMMARY -Encoding utf8
}
