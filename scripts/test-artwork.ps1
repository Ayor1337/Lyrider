param([switch]$NoBuild)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repoRoot 'tests/Lyrider.Artwork.Tests/Lyrider.Artwork.Tests.csproj'
if (-not $NoBuild) {
    dotnet build $project -p:Platform=x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Artwork test build failed.' }
}

$output = Join-Path $repoRoot 'tests/Lyrider.Artwork.Tests/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64'
$logPath = Join-Path $output 'results.log'
if (Test-Path -LiteralPath $logPath) { Remove-Item -LiteralPath $logPath }
$process = Start-Process -FilePath (Join-Path $output 'Lyrider.Artwork.Tests.exe') -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(45000)) {
    Stop-Process -Id $process.Id
    throw 'Artwork integration tests timed out.'
}
if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath }
if ($process.ExitCode -ne 0) { throw "Artwork integration tests failed (exit $($process.ExitCode))." }
