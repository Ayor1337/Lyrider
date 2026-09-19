param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateRange(1, 30)]
    [int]$AliveSeconds = 5
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$executablePath = Join-Path $repositoryRoot "src\Lyrider\bin\x64\$Configuration\net10.0-windows10.0.19041.0\Lyrider.exe"

if (-not (Test-Path -LiteralPath $executablePath)) {
    throw "启动验证找不到应用：$executablePath"
}

$process = Start-Process -FilePath $executablePath -PassThru -WindowStyle Hidden
try {
    if ($process.WaitForExit($AliveSeconds * 1000)) {
        throw "Lyrider 启动后提前退出，退出代码：$($process.ExitCode)"
    }

    Write-Host "Lyrider 启动验证通过：进程保持运行 $AliveSeconds 秒。"
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id
    }
}
