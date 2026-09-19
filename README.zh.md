# Lyrider

[English](README.md) | **简体中文**

Lyrider 是一个用于 [Cider](https://cider.sh) 的歌词显示工具，**仅支持 Windows 11 平台**。

## 当前功能

- 一个简易的播放列表
- 在任务栏显示歌词和歌曲信息
- 界面支持简体中文和英语，默认跟随 Windows 显示语言，也可在设置中切换

## 界面预览

### 主界面

![Lyrider 主界面](snapshots/main-window.png)

### 任务栏组件

<p align="center">
  <img src="snapshots/taskbar-lyrics.png" alt="任务栏歌词" width="49%">
  <img src="snapshots/taskbar-controls.png" alt="任务栏播放控制" width="49%">
</p>

## 运行方式

### 准备 Cider

安装并启动 [Cider](https://cider.sh)，然后在 Cider 设置中启用 Local API。Lyrider 默认连接 `http://localhost:10767/`；如果 Local API 开启了认证，还需要在 Lyrider 首次启动引导或设置页中填写 Cider 生成的 App Token。

### 安装 Lyrider

1. 从 [GitHub Releases](https://github.com/Ayor1337/Lyrider/releases/latest) 下载同一版本的 `Lyrider_<版本>_x64.msix` 和 `Lyrider.cer`。
2. 首次安装时，打开 PowerShell，进入下载目录并执行：

```powershell
Import-Certificate -FilePath .\Lyrider.cer -CertStoreLocation Cert:\CurrentUser\TrustedPeople
$package = Get-ChildItem -Filter 'Lyrider_*_x64.msix' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
Add-AppxPackage $package.FullName
```

也可以双击 `Lyrider.cer`，将证书安装到“当前用户”的“受信任人”存储，然后双击 MSIX 安装。只应信任从本仓库 Release 下载的证书。

安装包已包含 .NET 和 Windows App SDK 运行时，无需额外安装。后续版本只要继续使用同一张证书签名，直接安装新的 MSIX 即可升级，不需要重复导入证书。

## 通过源码运行

需要准备：

- Windows 11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Git

克隆仓库并在仓库根目录执行：

```powershell
git clone https://github.com/Ayor1337/Lyrider.git
cd Lyrider

dotnet restore .\Lyrider.sln -p:Platform=x64
dotnet build .\Lyrider.sln -p:Platform=x64 --no-restore
dotnet run --project .\src\Lyrider\Lyrider.csproj -p:Platform=x64
```

运行测试：

```powershell
dotnet test .\tests\Lyrider.Tests\Lyrider.Tests.csproj
```

测试项目没有加入 `Lyrider.sln`，因此必须直接指定测试项目；除非刚刚单独构建过该项目，否则不要使用 `--no-build`。重新构建 Debug 版本前请先退出正在运行的 Lyrider，避免可执行文件被占用。

## 开源协议

本项目基于 [MIT License](LICENSE) 开源。
