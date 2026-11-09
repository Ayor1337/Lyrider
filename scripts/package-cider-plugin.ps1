#Requires -Version 7.0
[CmdletBinding()]
param(
    # 同时把插件复制到 Cider 的插件目录，之后在 Cider 设置中启用即可。
    [switch]$Install
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$pluginDirectory = Join-Path $repoRoot 'cider-plugin'
$pluginFiles = 'plugin.yml', 'plugin.js'

$manifestText = Get-Content -LiteralPath (Join-Path $pluginDirectory 'plugin.yml') -Raw
$identifier = [regex]::Match($manifestText, '(?m)^identifier:\s*(\S+)\s*$').Groups[1].Value
$version = [regex]::Match($manifestText, '(?m)^version:\s*(\S+)\s*$').Groups[1].Value
if (-not $identifier -or -not $version) {
    throw 'plugin.yml 缺少 identifier 或 version。'
}

$scriptText = Get-Content -LiteralPath (Join-Path $pluginDirectory 'plugin.js') -Raw
if (-not $scriptText.Contains("`"$identifier`"")) {
    throw "plugin.js 中的 identifier 与 plugin.yml 不一致：$identifier"
}

$outputDirectory = Join-Path $repoRoot 'AppPackages'
New-Item -ItemType Directory $outputDirectory -Force | Out-Null
$zipPath = Join-Path $outputDirectory "Lyrider-Cider-Plugin_$version.zip"
# Cider 要求 plugin.yml 位于 zip 根目录。
Compress-Archive -Path ($pluginFiles | ForEach-Object { Join-Path $pluginDirectory $_ }) `
    -DestinationPath $zipPath -Force
Write-Host "已生成 Cider 插件：$zipPath"

if ($Install) {
    $ciderPlugins = Join-Path $env:APPDATA 'sh.cider.dotnet\plugins'
    if (-not (Test-Path $ciderPlugins -PathType Container)) {
        throw "未找到 Cider 插件目录：$ciderPlugins"
    }

    $installDirectory = Join-Path $ciderPlugins $identifier
    New-Item -ItemType Directory $installDirectory -Force | Out-Null
    foreach ($file in $pluginFiles) {
        Copy-Item -LiteralPath (Join-Path $pluginDirectory $file) -Destination $installDirectory -Force
    }
    Write-Host "已安装到：$installDirectory"
    Write-Host '重启 Cider，并在 设置 > 扩展 > 插件 中启用 Lyrider Launcher。'
}
