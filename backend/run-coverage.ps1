# Runs all three test projects with code coverage, then builds an HTML report.
# Requires the reportgenerator global tool: dotnet tool install -g dotnet-reportgenerator-globaltool
#
# Usage (from anywhere):
#   pwsh backend/run-coverage.ps1

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Get-ChildItem -Path . -Recurse -Directory -Filter "TestResults" -ErrorAction SilentlyContinue |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

$testProjects = @(
    "BookSpace.Application.Tests/BookSpace.Application.Tests.csproj",
    "BookSpace.Infrastructure.Tests/BookSpace.Infrastructure.Tests.csproj",
    "BookSpace.Api.Tests/BookSpace.Api.Tests.csproj"
)

foreach ($project in $testProjects) {
    dotnet test $project --collect:"XPlat Code Coverage" --settings coverlet.runsettings
    if ($LASTEXITCODE -ne 0) {
        throw "Tests failed for $project"
    }
}

$reports = "BookSpace.Application.Tests/TestResults/*/coverage.cobertura.xml;" +
           "BookSpace.Infrastructure.Tests/TestResults/*/coverage.cobertura.xml;" +
           "BookSpace.Api.Tests/TestResults/*/coverage.cobertura.xml"

reportgenerator "-reports:$reports" "-targetdir:CoverageReport" "-reporttypes:Html;TextSummary"

Get-Content "CoverageReport/Summary.txt"

Start-Process "CoverageReport/index.html"
