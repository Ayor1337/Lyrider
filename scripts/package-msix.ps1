#Requires -Version 7.0
<#
.SYNOPSIS
    构建 Lyrider 并打包为已签名的 MSIX。
.DESCRIPTION
    以自包含方式构建应用，把构建输出直接作为 MSIX 的包布局，补上 AppxManifest.xml 和
    应用图标后用 makeappx 打包，最后用当前用户证书存储中的自签名证书签名并导出 .cer。

    必须使用 dotnet build 的输出而不是 dotnet publish 的输出：publish 会漏掉
    App.xbf、MainWindow.xbf 和 Lyrider.pri，打出的包启动时会找不到 XAML 资源。
.EXAMPLE
    .\scripts\package-msix.ps1 -Version 1.0.1.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [ValidateSet('x64', 'ARM64')]
    [string]$Platform = 'x64',

    [string]$Configuration = 'Release',

    # 必须与签名证书的 Subject 完全一致
    [string]$Publisher = 'CN=Lyrider'
)

$ErrorActionPreference = 'Stop'

function Resolve-SdkTool {
    param([string]$Name)

    $binRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $sdkVersion = Get-ChildItem $binRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object Name -match '^\d+(\.\d+){3}$' |
        Sort-Object { [version]$_.Name } -Descending |
        Select-Object -First 1
    if (-not $sdkVersion) {
        throw "未找到 Windows SDK 工具目录：$binRoot"
    }

    $path = Join-Path $sdkVersion.FullName "x64\$Name"
    if (-not (Test-Path $path)) {
        throw "未找到 $Name：$path"
    }

    return $path
}

if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "版本号必须是 Major.Minor.Build.Revision 四段格式，当前为：$Version"
}

$repoRoot = Split-Path $PSScriptRoot -Parent
$projectPath = Join-Path $repoRoot 'src\Lyrider\Lyrider.csproj'
$runtimeIdentifier = if ($Platform -eq 'x64') { 'win-x64' } else { 'win-arm64' }
$manifestArch = if ($Platform -eq 'x64') { 'x64' } else { 'arm64' }

Write-Host "正在构建 Lyrider（$Configuration / $Platform / 自包含）..."
$buildArguments = @(
    'build', $projectPath,
    '-c', $Configuration,
    '-r', $runtimeIdentifier,
    '--self-contained',
    "-p:Platform=$Platform",
    '-p:WindowsAppSDKSelfContained=true',
    '--nologo'
)
& dotnet @buildArguments
if ($LASTEXITCODE -ne 0) {
    throw "构建失败，退出代码：$LASTEXITCODE"
}

$binRoot = Join-Path $repoRoot "src\Lyrider\bin\$Platform\$Configuration"
$appDirectory = Get-ChildItem $binRoot -Directory -ErrorAction SilentlyContinue |
    ForEach-Object { Join-Path $_.FullName $runtimeIdentifier } |
    Where-Object { Test-Path (Join-Path $_ 'Lyrider.exe') } |
    Select-Object -First 1
if (-not $appDirectory) {
    throw "未找到构建输出目录：$binRoot\<tfm>\$runtimeIdentifier\Lyrider.exe"
}

foreach ($file in 'Lyrider.exe', 'Lyrider.pri', 'App.xbf', 'MainWindow.xbf') {
    if (-not (Test-Path (Join-Path $appDirectory $file))) {
        throw "构建输出缺少 $file：$appDirectory"
    }
}

$packageName = "Lyrider_${Version}_${Platform}"
$outputDirectory = Join-Path $repoRoot "AppPackages\$packageName"
$layoutDirectory = Join-Path $outputDirectory 'layout'
if (Test-Path $outputDirectory) {
    Remove-Item $outputDirectory -Recurse -Force
}

Write-Host "正在生成包布局：$layoutDirectory"
New-Item -ItemType Directory $layoutDirectory -Force | Out-Null
Get-ChildItem $appDirectory |
    Where-Object Name -ne 'publish' |
    Copy-Item -Destination $layoutDirectory -Recurse -Force
New-Item -ItemType Directory (Join-Path $layoutDirectory 'Assets') -Force | Out-Null
Copy-Item (Join-Path $repoRoot 'src\Lyrider\Assets\Lyrider.png') (Join-Path $layoutDirectory 'Assets') -Force

$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  IgnorableNamespaces="uap desktop rescap">
  <Identity
    Name="Lyrider"
    Publisher="$Publisher"
    Version="$Version"
    ProcessorArchitecture="$manifestArch" />
  <Properties>
    <DisplayName>Lyrider</DisplayName>
    <PublisherDisplayName>Lyrider</PublisherDisplayName>
    <Logo>Assets\Lyrider.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily
      Name="Windows.Desktop"
      MinVersion="10.0.17763.0"
      MaxVersionTested="10.0.26100.0" />
  </Dependencies>
  <Applications>
    <Application
      Id="App"
      Executable="Lyrider.exe"
      EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements
        DisplayName="Lyrider"
        Description="Cider companion player"
        BackgroundColor="transparent"
        Square150x150Logo="Assets\Lyrider.png"
        Square44x44Logo="Assets\Lyrider.png" />
      <Extensions>
        <desktop:Extension
          Category="windows.fullTrustProcess"
          Executable="Lyrider.exe" />
      </Extensions>
    </Application>
  </Applications>
  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
</Package>
"@
Set-Content -Path (Join-Path $layoutDirectory 'AppxManifest.xml') -Value $manifest -Encoding utf8

$packagePath = Join-Path $outputDirectory "$packageName.msix"
Write-Host "正在打包：$packagePath"
& (Resolve-SdkTool 'makeappx.exe') pack /d $layoutDirectory /p $packagePath /o
if ($LASTEXITCODE -ne 0) {
    throw "makeappx 打包失败，退出代码：$LASTEXITCODE"
}

$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $Publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
if (-not $certificate) {
    Write-Host "当前用户证书存储中没有可用的 $Publisher 证书，正在创建自签名证书..."
    $certificateArguments = @{
        Type = 'Custom'
        Subject = $Publisher
        KeyUsage = 'DigitalSignature'
        FriendlyName = 'Lyrider MSIX'
        CertStoreLocation = 'Cert:\CurrentUser\My'
        TextExtension = @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    }
    $certificate = New-SelfSignedCertificate @certificateArguments
}

$certificatePath = Join-Path $outputDirectory 'Lyrider.cer'
Export-Certificate -Cert $certificate -FilePath $certificatePath -Type CERT | Out-Null

Write-Host '正在签名...'
& (Resolve-SdkTool 'signtool.exe') sign /fd SHA256 /td SHA256 /sha1 $certificate.Thumbprint $packagePath
if ($LASTEXITCODE -ne 0) {
    throw "签名失败，退出代码：$LASTEXITCODE"
}

Write-Host ''
Write-Host "MSIX：$packagePath"
Write-Host "证书：$certificatePath（$($certificate.Thumbprint)）"
Write-Host ''
Write-Host '首次安装前需要把证书加入当前用户的「受信任人」存储（无需管理员）：'
Write-Host "  Import-Certificate -FilePath '$certificatePath' -CertStoreLocation Cert:\CurrentUser\TrustedPeople"
Write-Host '然后安装：'
Write-Host "  Add-AppxPackage '$packagePath'"
