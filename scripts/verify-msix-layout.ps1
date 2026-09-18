#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$LayoutDirectory,

    [string]$MakePriPath
)

$ErrorActionPreference = 'Stop'

function Get-PngDimensions {
    param([Parameter(Mandatory)][string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $pngSignature = '89-50-4E-47-0D-0A-1A-0A'
    if ($bytes.Length -lt 24 -or [BitConverter]::ToString($bytes, 0, 8) -ne $pngSignature) {
        throw "不是有效的 PNG 文件：$Path"
    }

    $width = ([int]$bytes[16] -shl 24) -bor ([int]$bytes[17] -shl 16) -bor ([int]$bytes[18] -shl 8) -bor [int]$bytes[19]
    $height = ([int]$bytes[20] -shl 24) -bor ([int]$bytes[21] -shl 16) -bor ([int]$bytes[22] -shl 8) -bor [int]$bytes[23]
    return [pscustomobject]@{ Width = $width; Height = $height }
}

function Resolve-MakePri {
    $binRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $path = Get-ChildItem $binRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object Name -match '^\d+(\.\d+){3}$' |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\makepri.exe' } |
        Where-Object { Test-Path $_ -PathType Leaf } |
        Select-Object -First 1
    if (-not $path) {
        throw "未在 Windows SDK 工具目录中找到 makepri.exe：$binRoot"
    }
    return $path
}

$layoutPath = (Resolve-Path $LayoutDirectory).Path
$manifestPath = Join-Path $layoutPath 'AppxManifest.xml'
if (-not (Test-Path $manifestPath -PathType Leaf)) {
    throw "包布局缺少 AppxManifest.xml：$layoutPath"
}

[xml]$manifest = Get-Content -LiteralPath $manifestPath
$namespace = New-Object System.Xml.XmlNamespaceManager($manifest.NameTable)
$namespace.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
$visualElements = $manifest.SelectSingleNode('//uap:VisualElements', $namespace)
if (-not $visualElements) {
    throw 'AppxManifest.xml 缺少 uap:VisualElements。'
}

$logoRelativePath = $visualElements.GetAttribute('Square44x44Logo')
if (-not $logoRelativePath) {
    throw 'uap:VisualElements 缺少 Square44x44Logo。'
}
if ($visualElements.GetAttribute('BackgroundColor') -ne 'transparent') {
    throw '任务栏无底板图标要求 BackgroundColor="transparent"。'
}

$logoDirectory = Join-Path $layoutPath ([System.IO.Path]::GetDirectoryName($logoRelativePath))
$logoBaseName = [System.IO.Path]::GetFileNameWithoutExtension($logoRelativePath)
$targetSizes = 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256
$alternateForms = 'unplated', 'lightunplated'

foreach ($size in $targetSizes) {
    foreach ($alternateForm in $alternateForms) {
        $assetPath = Join-Path $logoDirectory "$logoBaseName.targetsize-${size}_altform-$alternateForm.png"
        if (-not (Test-Path $assetPath -PathType Leaf)) {
            throw "包布局缺少任务栏图标资源：$assetPath"
        }

        $dimensions = Get-PngDimensions $assetPath
        if ($dimensions.Width -ne $size -or $dimensions.Height -ne $size) {
            throw "任务栏图标尺寸错误：$assetPath（$($dimensions.Width)x$($dimensions.Height)，应为 ${size}x${size}）"
        }
    }
}

if (-not (Test-Path (Join-Path $layoutPath 'resources.pri') -PathType Leaf)) {
    throw '包布局缺少 resources.pri，Windows 无法选择 targetsize/altform 图标资源。'
}

if (-not $MakePriPath) {
    $MakePriPath = Resolve-MakePri
}
$priPath = Join-Path $layoutPath 'resources.pri'
$dumpPath = Join-Path ([System.IO.Path]::GetTempPath()) "Lyrider-pri-$([Guid]::NewGuid().ToString('N')).xml"
try {
    & $MakePriPath dump /if $priPath /of $dumpPath /o | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "makepri 无法读取包资源索引，退出代码：$LASTEXITCODE"
    }

    $priDump = Get-Content -LiteralPath $dumpPath -Raw
    foreach ($xbfResource in 'App.xbf', 'MainWindow.xbf') {
        $resourcePattern = 'NamedResource name="{0}"' -f [regex]::Escape($xbfResource)
        if ($priDump -notmatch $resourcePattern) {
            throw "resources.pri 缺少 WinUI XAML 资源：$xbfResource"
        }
    }
}
finally {
    Remove-Item -LiteralPath $dumpPath -Force -ErrorAction SilentlyContinue
}

Write-Host "MSIX 图标资源验证通过：$($targetSizes.Count * $alternateForms.Count) 个无底板目标尺寸资源。"
Write-Host 'MSIX WinUI 资源验证通过：App.xbf、MainWindow.xbf 已合并到 resources.pri。'
