# Lyrider

Lyrider 是一个使用 C#、WinUI 3、Windows App SDK 和 XAML 编写的 Windows 原生应用。当前版本每秒读取一次本机 Cider Local API，并显示正在播放的歌曲。

## 当前功能

- 显示专辑封面、歌曲名、歌手和专辑名
- 显示当前播放时间、总时长和播放进度
- 显示 Cider 连接、认证及响应错误状态
- 支持保存 Cider App Token；Token 使用 Windows DPAPI 加密，不写入日志

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

应用启动后会访问：

```text
GET http://localhost:10767/api/v1/playback/now-playing
```

如果 Cider 开启了 API 认证，在窗口底部输入由 Cider 生成的 App Token，然后点击“保存并应用”。请求会通过 `apptoken` 请求头传递 Token。留空后保存会删除已有 Token，后续请求不会发送该请求头。

Token 通过 Windows DPAPI 绑定到当前 Windows 用户并加密存储在：

```text
%LOCALAPPDATA%\Lyrider\cider-token.dat
```

应用不会记录或明文保存 Token。

## 项目结构

```text
src/Lyrider/
├── Models/                   Cider API 响应模型
├── Services/CiderService.cs  HTTP 请求、解析和错误处理
├── App.xaml                  应用入口
└── MainWindow.xaml           Now Playing 界面和轮询调度
```

当前版本不包含歌词、播放控制、Socket.IO、系统托盘、任务栏集成或设置持久化。
