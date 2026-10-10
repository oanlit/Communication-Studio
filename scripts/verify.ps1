param([switch]$SkipTests, [string]$ArtifactsPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    $outputArguments = @()
    if ($ArtifactsPath) { $outputArguments = @('--artifacts-path', $ArtifactsPath) }
    dotnet build CommunicationStudio.slnx --configuration Release --nologo --disable-build-servers @outputArguments
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    if (-not $SkipTests) {
        if ($ArtifactsPath) {
            dotnet (Join-Path $ArtifactsPath 'bin/Communication.Tests/release/Communication.Tests.dll')
        } else {
            dotnet run --project tests/Communication.Tests --configuration Release --no-build
        }
        if ($LASTEXITCODE -ne 0) { throw 'Integration checks failed' }
    }
}
finally { Pop-Location }
