# Lyrider

Lyrider 是一个使用 C#、WinUI 3、Windows App SDK 和 XAML 编写的 Windows 原生 Cider 伴侣播放器。界面采用自适应布局：宽窗口左侧显示当前歌曲和基础播放控制，右侧在时间轴歌词与当前播放队列之间切换；窄窗口自动上下排列。

## 当前功能

- 显示专辑封面、歌曲名、歌手、专辑、播放时间和进度
- 显示当前播放队列，并支持点击队列项目切换歌曲
- 显示 Cider 提供的时间轴歌词，自动突出并滚动到当前行
- 支持点击时间轴歌词跳转播放位置
- 提供播放/暂停、上一首、下一首、随机、循环和音量控制
- 可选的 Windows 11 任务栏播放条：在系统小组件旁显示封面、歌名和歌手，悬浮后切换为播放控制
- 支持系统托盘图标；可设置点击窗口关闭按钮时最小化到托盘，双击托盘图标可恢复窗口
- 显示 Cider 连接、认证及响应错误状态
- 标题栏左上角提供三点悬浮菜单，可查看 Cider 连接状态并切换队列、歌词、设置或退出应用
- 提供独立设置页，可调整主题、歌词字号、背景强度、默认视图、音量控件、窗口置顶和关闭行为
- 支持配置 Cider API 地址并保存 App Token；Token 使用 Windows DPAPI 加密，不写入日志

## 窗口与响应式布局

- 最小客户区为 480 × 600 DIP（逻辑像素），窗口边框额外计算；尺寸约束随显示器 DPI 更新。
- 启动客户区目标尺寸为 1360 × 820 DIP，按当前屏幕工作区缩小，预留窗口边框和任务栏空间。
- 客户区宽度低于 1000 DIP 时，播放器与歌词/队列上下排列；队列隐藏重复的当前歌曲信息，列表与歌词保持独立滚动。
- 封面随窗口宽高缩放，空间不足时隐藏封面，优先保留播放控件。
- 宽度低于 760 DIP 时，设置项的说明与控件上下排列；设置页可纵向滚动。
- 最小尺寸和布局断点由应用统一管理，不增加需要用户调节的设置项。

## 环境要求

- Windows 10 版本 1809（Build 17763）或更高版本
- .NET 10 SDK（构建）或 .NET 10 Desktop Runtime（运行）
- Visual Studio 2026，并安装 WinUI 应用开发工作负载；或可构建 WinUI 3 项目的等效工具链
- Windows App SDK 2.4 Runtime（框架依赖的 unpackaged 应用需要）
- Cider 已启动并启用 Local API（使用播放功能时需要）

## 运行

```powershell
dotnet restore .\Lyrider.sln
dotnet build .\Lyrider.sln -p:Platform=x64
dotnet run --project .\src\Lyrider\Lyrider.csproj -p:Platform=x64
dotnet test .\tests\Lyrider.Tests\Lyrider.Tests.csproj --no-restore
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

Cider 未启动时，Lyrider 仍可正常打开并显示连接失败状态；启动 Cider 后会自动重试连接。

如果 Cider 开启了 API 认证，在设置页输入由 Cider 生成的 App Token，然后保存设置。请求会通过 `apptoken` 请求头传递 Token。留空后保存会删除已有 Token，后续请求不会发送该请求头。

不同 Cider 版本对歌词接口的支持程度不同。接口不可用或歌曲没有歌词时，Lyrider 会保留播放器和队列功能，并显示无歌词状态。

任务栏播放状态默认关闭，可在设置页启用。该功能仅在 Windows 11 的主显示器水平任务栏上运行：播放条作为透明子窗口挂载到系统任务栏，并优先显示在原生 Widgets 按钮旁。未悬浮时，只有播放条背景对应的 216 × 40 DIP 区域会触发控制栏。Cider 未连接或没有当前歌曲时播放条会自动隐藏；Explorer 重启后会自动重新挂载。由于任务栏结构不是公开扩展接口，后续 Windows 更新或第三方任务栏修改工具可能导致定位暂时失效，此时 Lyrider 会隐藏播放条并继续重试，不会覆盖未知的系统控件。

“关闭时最小化到托盘”默认关闭，以保持直接关闭窗口的原有行为。启用后，点击标题栏关闭按钮会隐藏窗口；双击托盘图标或使用托盘菜单中的“显示 Lyrider”可恢复窗口。三点应用菜单和托盘菜单中的“退出 Lyrider”始终会直接退出应用。

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
src/Lyrider.TaskbarWidget/    桌面互操作：系统托盘、WPF 任务栏子窗口与 Shell 定位
tests/Lyrider.Tests/          队列刷新与响应解析回归测试
```

当前版本使用 HTTP 轮询，不包含 Socket.IO；任务栏播放条复用主窗口的轮询结果，不会额外请求 Cider API。
