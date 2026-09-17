# Lyrider

Lyrider 是一个使用 C#、WinUI 3、Windows App SDK 和 XAML 编写的 Windows 原生 Cider 伴侣播放器。界面采用沉浸式双栏布局：左侧显示当前歌曲和基础播放控制，右侧在时间轴歌词与当前播放队列之间切换。

## 当前功能

- 显示专辑封面、歌曲名、歌手、专辑、播放时间和进度
- 显示当前播放队列，并支持点击队列项目切换歌曲
- 显示 Cider 提供的时间轴歌词，自动突出并滚动到当前行
- 支持点击时间轴歌词跳转播放位置
- 提供播放/暂停、上一首、下一首、随机、循环和音量控制
- 显示 Cider 连接、认证及响应错误状态
- 提供独立设置页，可调整主题、歌词字号、背景强度、默认视图、音量控件和窗口置顶
- 支持配置 Cider API 地址并保存 App Token；Token 使用 Windows DPAPI 加密，不写入日志

## 环境要求

- Windows 10 版本 1809（Build 17763）或更高版本
- .NET 10 SDK
- Visual Studio 2026，并安装 WinUI 应用开发工作负载；或可构建 WinUI 3 项目的等效工具链
- Windows App SDK 2.4 Runtime（框架依赖的 unpackaged 应用需要）
- Cider 已启动，并启用 Local API

## 运行

```powershell
dotnet restore .\Lyrider.sln
dotnet build .\Lyrider.sln -p:Platform=x64
dotnet run --project .\src\Lyrider\Lyrider.csproj -p:Platform=x64
```

也可以在 Visual Studio 中打开 `Lyrider.sln`，选择与系统匹配的平台后直接运行。

应用启动后主要访问以下 Cider Local API：

```text
GET http://localhost:10767/api/v1/playback/now-playing
GET http://localhost:10767/api/v1/playback/queue
GET http://localhost:10767/api/v1/playback/is-playing
GET http://localhost:10767/api/v1/playback/volume
GET http://localhost:10767/api/v1/lyrics/{trackId}
```

如果 Cider 开启了 API 认证，在设置页输入由 Cider 生成的 App Token，然后保存设置。请求会通过 `apptoken` 请求头传递 Token。留空后保存会删除已有 Token，后续请求不会发送该请求头。

不同 Cider 版本对歌词接口的支持程度不同。接口不可用或歌曲没有歌词时，Lyrider 会保留播放器和队列功能，并显示无歌词状态。

Token 通过 Windows DPAPI 绑定到当前 Windows 用户并加密存储在：

```text
%LOCALAPPDATA%\Lyrider\cider-token.dat
```

应用不会记录或明文保存 Token。

其他界面设置保存在：

```text
%LOCALAPPDATA%\Lyrider\settings.json
```

## 项目结构

```text
src/Lyrider/
├── Models/                   Cider API 响应模型
├── Services/CiderService.cs  HTTP 请求、播放控制、兼容解析和错误处理
├── Services/SettingsStore.cs 非敏感界面设置持久化
├── Services/TokenStore.cs    DPAPI Token 持久化
├── App.xaml                  应用入口
└── MainWindow.xaml           沉浸式播放器、歌词、队列和设置界面
```

当前版本使用 HTTP 轮询，不包含 Socket.IO、系统托盘或任务栏集成。
