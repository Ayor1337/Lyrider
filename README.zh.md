# Lyrider

[English](README.md) | **简体中文**

Lyrider 是一个用于 [Cider](https://cider.sh) 的歌词显示工具，**仅支持 Windows 11 平台**。

## 当前功能

- 一个简易的播放列表
- 在任务栏显示歌词和歌曲信息
- 透明置顶桌面歌词，支持翻译、拖动缩放、锁定穿透和托盘操作
- 使用本地同步播放时钟实现低延迟歌词切换
- 可设置优先歌词源，在 Cider、网易云音乐、QQ 音乐、Musixmatch 和 LRCLIB 之间自动回退
- 主界面显示当前歌词的实际来源
- 可显示歌词源提供的简体中文译文
- 界面支持简体中文和英语，默认跟随 Windows 显示语言，也可在设置中切换
- 设置页的「实验」分区：Windows 任务栏图标靠左时，可把任务栏歌词切成右对齐样式（歌词在封面左侧右对齐，封面靠右）

## 界面预览

### 主界面

![Lyrider 主界面及实际歌词来源](snapshots/main-lyrics-source.png)

### 任务栏组件

<p align="center">
  <img src="snapshots/taskbar-lyrics.png" alt="任务栏歌词" width="49%">
  <img src="snapshots/taskbar-controls.png" alt="任务栏播放控制" width="49%">
</p>

### 桌面歌词

![桌面歌词](snapshots/desktop-lyrics.png)

![桌面歌词右键菜单](snapshots/desktop-lyrics-menu.png)

### 托盘菜单

<p align="center">
  <img src="snapshots/tray-menu-dark.png" alt="深色托盘菜单" width="200">
  <img src="snapshots/tray-menu-light.png" alt="浅色托盘菜单" width="200">
</p>

## 运行方式

### 准备 Cider

安装并启动 [Cider](https://cider.sh)，然后在 Cider 设置中启用 Local API。Lyrider 默认连接 `http://localhost:10767/`；如果 Local API 开启了认证，还需要在 Lyrider 首次启动引导或设置页中填写 Cider 生成的 App Token。

### 歌词源与翻译

默认的“自动”模式会依次尝试网易云音乐、QQ 音乐、Cider、Musixmatch 和 LRCLIB。在设置中指定“优先歌词源”后，会先尝试该来源，没有合适结果时继续查找其他来源。已有的 Cider 歌词可在查询期间先显示，找到更好的结果后再替换，查询期间继续刷新播放状态。歌曲信息右侧会显示当前歌词的实际来源，自动回退和预加载结果也会同步更新；换曲加载、无歌词或断开连接时隐藏来源名称。

开启翻译后，优先查找逐行同步且中文译文覆盖至少 80% 非空歌词行的结果，原文以中文为主时无需额外译文；没有达标结果时保留已有候选中更合适的一份。选择 Cider 作为优先源时也可以开启翻译，由其他来源补充缺少的译文。每个远程歌词源独立限时 4 秒。Musixmatch 只有配置 API Key 后才参与查询；密钥使用 Windows DPAPI 加密保存在本机，不会写入普通设置文件。

翻译功能只显示歌词源自带的简体中文译文，不会对缺失内容进行机器翻译。开启后，主歌词页会在每句原文下方显示译文；任务栏会在当前原文下方显示译文。原文以中文为主或当前句没有译文时，Lyrider 会保持当前句/下一句布局。外部同步歌词与 Cider 至少有三条唯一歌词文本相同时，会自动对齐到 Cider 时间轴。对于 Apple Music 在不同商店使用不同标题或歌手名的曲目，Lyrider 会使用曲目 ID 查询 Apple 日本商店元数据作为额外搜索别名；没有曲目 ID 时，只会在歌手和时长唯一对应时接受不同标题，避免误取同歌手的其他版本。网易云音乐与 QQ 音乐依赖非官方 Web 接口，提供方变更接口后可能暂时失效。

### 桌面歌词

在设置页开启“桌面歌词”并保存后，歌词会显示在普通应用上方。双行和翻译默认开启；非中文歌曲有译文时显示当前原文和译文，单行模式也保留译文。没有可用译文时，双行显示当前句和下一句，单行只显示当前句。桌面翻译独立于主歌词页和任务栏的翻译开关。歌词加载中或没有同步歌词时显示歌名，Cider 断开或没有歌曲时隐藏。

文字方向可选横排或竖排，对齐方式可选居中、左右分离、左对齐或右对齐。左右分离时，两句原文分居两侧并轮流播放：下一句开始时保持原位，唱完的位置换成新的下一句。翻译模式、单行模式和歌名在选择左右分离时按居中显示。歌词整体垂直居中，行距随窗口高度调整；升级后会保留已有的布局偏好。

设置页提供实时预览，可切换普通歌词和带翻译场景。可以选择配色预设，也可分别设置已唱、未唱颜色。两行使用相同字号，字重可选常规、半粗或粗体；描边颜色和粗细可单独设置，粗细范围为 0～8 DIP，0 表示关闭。翻译模式下两行都使用已唱颜色；其他模式下，当前句使用已唱颜色，下一句使用未唱颜色。

开启“逐字显示”并保存后，当前原文会按 Cider 提供的真实字词时间扫色，暂停时停在当前位置。Lyrider 优先保留带逐字时间的歌词，并可从其他来源补充文本匹配的译文。没有有效逐字时间时整句高亮，不估算字词进度。翻译模式下两行使用已唱颜色，不进行逐字扫色。

首次开启时可以拖动窗口，使用右侧、底部或右下角手柄调整尺寸。未锁定时，悬停会在窗口内显示上一首、播放/暂停、下一首、锁定和关闭按钮。右键可直接调整字号、对齐、单双行、文字方向、翻译和逐字显示，也可锁定、重置位置或关闭歌词；这些快捷操作立即生效并保存到本机。

锁定后隐藏背景，鼠标操作穿透到下方应用；悬停时显示可点击的解锁图标，也可在托盘菜单取消勾选“锁定桌面歌词”。托盘菜单用勾选表示显示和锁定状态，并提供设置和位置重置入口。字号、背景不透明度和暂停隐藏可独立设置。位置、尺寸和锁定状态会保存在本机；重置位置恢复默认宽度和随内容变化的高度。隐藏主窗口后桌面歌词继续显示，退出 Lyrider 时一并关闭。

窗口缩放范围为宽度 320～1200 DIP、高度 80～480 DIP，字号范围为 16～72 DIP。窗口不能超过当前显示器工作区，最小高度会保留容纳歌词所需的空间。

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

安装包已包含 .NET、Windows App SDK 运行时和两套界面语言资源；语言资源会保留在主包中，因此设置页的语言选择不受 Windows 当前显示语言限制。后续版本只要继续使用同一张证书签名，直接安装新的 MSIX 即可升级，不需要重复导入证书。

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
