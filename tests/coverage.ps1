# Runs the unit tests with coverage and prints a per-library summary.
# Usage (from the repository root):  pwsh tests/coverage.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$results = Join-Path $PSScriptRoot 'TestResults'
if (Test-Path $results) { Remove-Item $results -Recurse -Force }

$projects = @{ 'viddl.Core' = 'viddl.Core.Tests'; 'viddl.Downloads' = 'viddl.Downloads.Tests' }
foreach ($lib in $projects.Keys | Sort-Object) {
    $testProject = Join-Path $PSScriptRoot $projects[$lib]
    $out = Join-Path $results $lib
    dotnet test $testProject --collect:"XPlat Code Coverage" --results-directory $out | Select-String 'Passed!|Failed!'
    if ($LASTEXITCODE -ne 0) { throw "Tests failed for $lib" }

    $report = Get-ChildItem $out -Recurse -Filter coverage.cobertura.xml | Select-Object -First 1
    [xml]$xml = Get-Content $report.FullName
    $package = $xml.coverage.packages.package | Where-Object name -eq $lib
    $lines = @($package.classes.class.lines.line)
    $covered = @($lines | Where-Object hits -ne '0').Count
    '{0,-16} lines {1,6:P1} ({2}/{3})   branches {4,6:P1}' -f $lib, [double]$package.'line-rate', $covered, $lines.Count, [double]$package.'branch-rate'
    foreach ($class in $package.classes.class) {
        $missed = @($class.lines.line | Where-Object hits -eq '0' | ForEach-Object number | Sort-Object { [int]$_ } -Unique)
        if ($missed.Count) { '    not covered: {0} line {1}' -f $class.filename, ($missed -join ',') }
    }
}
