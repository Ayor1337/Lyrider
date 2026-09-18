# Lyrider

Lyrider 是一个使用 C#、WinUI 3、Windows App SDK 和 XAML 编写的 Windows 原生 Cider 伴侣播放器。界面采用自适应布局：宽窗口左侧显示当前歌曲和基础播放控制，右侧在时间轴歌词与当前播放队列之间切换；窄窗口自动上下排列。

## 当前功能

- 显示专辑封面、歌曲名、歌手、专辑、播放时间和进度
- 首次进入播放器时显示轻量加载动画，首轮 Cider 状态刷新完成后淡出，不使用骨架屏
- 播放器背景使用当前封面：强度（0–100%）控制封面在背景中的可见程度，模糊（0–100%）用 Win2D 高斯模糊把封面柔化成底色，0% 保持原图清晰
- 显示当前播放队列，并支持点击队列项目切换歌曲
- 显示时间轴歌词，优先使用 Cider，并在其歌词接口不可用时回退到 LRCLIB；当前行放大加粗、相邻行递减淡化，自动滚动时把当前行停在视口上方约三分之一处并平滑跟随
- 支持点击时间轴歌词跳转播放位置：悬停高亮并显示目标时间，点击后立即高亮该行并滚动到位，无需等待下一次轮询
- 提供播放/暂停、上一首、下一首、随机、循环和音量控制，音量滑杆右侧实时显示当前音量的百分比
- 可选的 Windows 11 任务栏播放条：在系统小组件旁显示封面、当前歌词和下一句，当前句过长时无缝循环滚动，悬浮后切换为播放控制；可关闭任务栏歌词并固定显示歌名和歌手
- 支持系统托盘图标和跟随应用主题的快捷菜单，外观与 Windows 11 原生菜单一致：亚克力材质背景、8 px 圆角、32 px 行高与悬停/键盘焦点反馈，不含图标，菜单左上角与光标重合；亚克力由 DWM 提供（要求 Windows 11 Build 22621 及以上），旧系统自动回退为不透明纯色；可设置点击窗口关闭按钮时最小化到托盘，双击托盘图标可恢复窗口
- 显示 Cider 连接、认证及响应错误状态
- 标题栏左上角提供三点悬浮菜单，可查看 Cider 连接状态并切换队列、歌词、设置或退出应用
- 提供 Windows 11 风格的 Mica 分层与 Fluent 设置卡片，可调整主题、歌词字号（20–96 px）、背景封面强度与模糊、默认视图、音量控件、窗口置顶和关闭行为；滑杆右侧实时显示当前的百分比或像素值
- 支持配置 Cider API 地址并保存 App Token；Token 使用 Windows DPAPI 加密，不写入日志

## 窗口与响应式布局

- 最小客户区为 480 × 600 DIP（逻辑像素），窗口边框额外计算；尺寸约束随显示器 DPI 更新。
- 启动客户区目标尺寸为 1360 × 820 DIP，按当前屏幕工作区缩小，预留窗口边框和任务栏空间。
- 客户区宽度低于 1000 DIP 时，播放器与歌词/队列上下排列；队列隐藏重复的当前歌曲信息，列表与歌词保持独立滚动。
- 封面随窗口宽高缩放，空间不足时隐藏封面，优先保留播放控件。
- 宽度低于 820 DIP 时，Cider 连接字段改为上下排列；低于 720 DIP 时，设置项的说明与控件也改为上下排列。设置页内容水平居中并支持纵向滚动，滚动条位于页面右侧；标题与内容左缘对齐，返回按钮和标题固定在页面顶部。进入和退出设置页时会播放短暂的淡入淡出与位移动画。
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

不同 Cider 版本对歌词接口的支持程度不同：Cider 4 的歌词由 `/api/v2/lyrics/{id}` 提供，要求 App Token 具备歌词权限，Lyrider 会按 `/api/v2`、`/api/v1` 的顺序尝试，以兼容较早版本。Cider 未返回歌词时，Lyrider 会按歌名、歌手、专辑和时长向 LRCLIB 查询；若两个来源都不可用或歌曲没有歌词，播放器和队列功能不受影响，并显示无歌词状态。LRCLIB 查询只在切歌或强制刷新歌曲详情时发生。

任务栏播放状态默认关闭，可在设置页启用。该功能仅在 Windows 11 的主显示器水平任务栏上运行：播放条作为透明子窗口挂载到系统任务栏；任务栏居中对齐时优先显示在原生 Widgets 按钮旁，切换为左对齐时自动移动到系统托盘左侧。“任务栏显示歌词”默认开启，时间轴歌词可用时显示当前句和下一句，当前句过长时会在 216 DIP 宽度内无缝循环滚动；关闭该设置，或歌词尚未开始、不可用、不带时间轴时，回退显示歌名和歌手。悬浮播放条会暂停歌词滚动并切换为播放控制。未悬浮时，只有播放条背景对应的 216 × 40 DIP 区域会触发控制栏。Cider 未连接或没有当前歌曲时播放条会自动隐藏；Explorer 重启后会自动重新挂载。无法可靠识别系统控件边界时，Lyrider 会隐藏播放条并继续重试，而不是覆盖通知区域；后续 Windows 更新或第三方任务栏修改工具仍可能导致定位暂时失效。

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

## 打包

```powershell
.\scripts\package-msix.ps1 -Version 1.0.1.0
```

脚本以自包含方式构建 x64 应用，把构建输出作为包布局并生成 `AppxManifest.xml`；它会从现有 ICO 生成深色和浅色主题共用的无底板任务栏图标，并用 `makepri` 建立资源索引，避免安装版图标被 Windows 缩小并添加系统底板。随后脚本交给 `makeappx` 打包，最后用当前用户证书存储中的 `CN=Lyrider` 自签名证书签名（证书不存在时自动创建）。产物在 `AppPackages\Lyrider_<版本>_<架构>\`，包含 `.msix` 和导出的 `Lyrider.cer`。

包是自包含的，目标机器不需要预装 .NET 与 Windows App SDK 运行时，但需要先信任该证书：

```powershell
Import-Certificate -FilePath .\AppPackages\Lyrider_1.0.1.0_x64\Lyrider.cer -CertStoreLocation Cert:\CurrentUser\TrustedPeople
Add-AppxPackage .\AppPackages\Lyrider_1.0.1.0_x64\Lyrider_1.0.1.0_x64.msix
```

打包必须使用 `dotnet build` 的输出：本项目的 `dotnet publish` 会漏掉 `App.xbf`、`MainWindow.xbf` 和 `Lyrider.pri`，用它打出的包启动时会找不到 XAML 资源。

## 项目结构

```text
src/Lyrider/
├── Models/                   Cider API 响应模型
├── Services/ArtworkBackdrop.cs Win2D 合成效果实现的模糊背景层
├── Services/CiderService.cs  HTTP 请求、播放控制、兼容解析和错误处理
├── Services/SettingsStore.cs 非敏感界面设置持久化
├── Services/TokenStore.cs    DPAPI Token 持久化
├── App.xaml                  应用入口
└── MainWindow.xaml           沉浸式播放器、歌词、队列和设置界面
src/Lyrider.TaskbarWidget/    桌面互操作：系统托盘、WPF 任务栏子窗口与 Shell 定位
tests/Lyrider.Tests/          队列刷新与响应解析回归测试
scripts/package-msix.ps1      MSIX 构建、打包与自签名脚本
```

当前版本使用 HTTP 轮询，不包含 Socket.IO；任务栏播放条复用主窗口的轮询结果，不会额外请求 Cider API。
