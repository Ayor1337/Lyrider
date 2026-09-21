# Lyrider 预加载歌词、封面与歌曲信息可行性调查

调查日期：2026-09-21

## 结论

可以添加，而且不需要改变 Cider 的播放行为：让 Lyrider 从队列读出下一首（或前几首）的歌曲资料，先在 Lyrider 自己的内存缓存中准备歌曲信息、封面和歌词；当前歌曲切换时优先从缓存提交到 UI。可行性分层如下：

| 内容 | 可用来源 | 可行性 | 主要限制 |
| --- | --- | --- | --- |
| 下一首歌曲信息 | Cider API v2 `GET /api/v2/queue`；队列项含 `track` 及其 `attributes` | 高 | 队列变更事件只带位置/总数，仍需重新拉队列；当前代码使用 v1 队列语义，含历史和当前项 |
| 下一首封面 | 队列项的 `track.attributes.artwork.url`；当前歌曲 API 也明确返回 artwork URL | 高 | CDN 图片需要另行加载；当前队列 UI 将 URL 规格化为 160px，主界面为 800px，不能把 UI 已加载的 160px 结果当作主图缓存 |
| Cider 歌词 | Cider API v2 `GET /api/v2/lyrics/:id`，支持任意歌曲 ID，返回带时间的 `lines[]` | 中高 | Cider 官方文档说明冷请求可能因顺序尝试 provider 耗时约 55 秒；需要超时、并发上限和失败降级 |
| Lyrider 外部歌词源/翻译 | 现有 `LyricsService.ResolveAsync` | 中 | 当前实现总超时 12 秒、单 provider 4 秒，并可能遍历 NetEase、QQ Music、Musixmatch、LRCLIB；预加载不能阻塞当前歌曲 |
| 及时触发 | Cider Socket.IO `API:Playback` | 高（Cider 4/v2） | 需要新增 Socket.IO 客户端；需要保留 HTTP 轮询作为断线/旧版 Cider 回退 |

因此推荐先做“下一首 1 首的歌曲信息 + 封面 + Cider 歌词”预加载，再扩展到外部歌词和翻译。预加载应是 best-effort：失败只表示切歌时重新获取，不得让当前播放或 UI 刷新失败。

## Cider 官方能力

### 队列、当前歌曲与封面

当前 Cider API 文档将 `GET /api/v2/queue` 定义为分页队列查询，响应带 `data.items`、`data.position` 和 `meta { offset, limit, total }`；文档说明队列是本机队列，单页 `limit` 上限为 200，不受 Apple Music 的 100 项限制。每个示例队列项都有 `track`，其中 `id`、`type` 和 `attributes` 可承载歌曲资料，因而可以在实际播放前拿到下一首的 ID 和展示所需的元数据。官方文档还提供 `GET /api/v2/queue/position`，可单独读取当前位置和总数。

来源：[Cider API Queue 文档](https://taproom.cider.sh/docs/api/queue)。官方维护的 [CiderDeck v2 客户端](https://github.com/ciderapp/CiderDeck/blob/main/src/services/cider-client.ts#L1384-L1434)也直接实现了 `GET /api/v2/queue`、`GET /api/v2/queue/position` 及队列操作。

`GET /api/v2/playback` 的官方示例同时返回当前歌曲的 `name`、`artistName`、`albumName`、`durationInMillis` 和 `artwork.url`；`GET /api/v2/playback/now-playing`则提供只读当前歌曲信息的轻量版本。也就是说，歌曲信息和封面 URL 不需要通过搜索再次解析。

来源：[Cider API Playback 文档](https://taproom.cider.sh/docs/api/playback)、[CiderDeck API 客户端](https://github.com/ciderapp/CiderDeck/blob/main/src/services/cider-client.ts#L1151-L1163)。

### 歌词按 ID 获取

当前文档提供：

```text
GET /api/v2/lyrics/:id
```

它接受 catalog 或 library song ID，不要求歌曲正在播放，返回 `source` 和 `lines[]`；行包含 `start`、`end`、`text`，可选 `words` 和原始 `ttml`。因此“队列 ID → 预取歌词”在 API 层是直接支持的。`GET /api/v2/lyrics/current` 只适合读取当前歌曲，且官方特别说明它通常读取应用歌词页面已经填充的 warm cache；预加载功能应把按 ID 请求的结果放入 Lyrider 自己的缓存，不能假定请求会自动使 `/lyrics/current` 可用。

官方文档还明确警告：按 ID 的冷请求会顺序尝试歌词 provider，冷歌曲可能耗时几十秒，最坏约 55 秒，并可能返回 `LYRICS_NOT_FOUND` 或 `LYRICS_TIMEOUT`。这使“提前请求”有价值，但也决定了必须限制预加载数量、设置独立超时、缓存负结果并避免占用当前歌曲请求。

来源：[Cider API Lyrics 文档](https://taproom.cider.sh/docs/api/lyrics)。Cider 4 的文档入口由官方 [Cider-2 `Cider4-Developer-API.md`](https://github.com/ciderapp/Cider-2/blob/main/docs/Cider4-Developer-API.md)指向该页面。

### 事件机制

Cider API 官方事件文档说明：同一 host/port 通过 Socket.IO 广播 `API:Playback`，消息形状为 `{ type, data }`。与预加载直接相关的事件包括：

- `playbackStatus.nowPlayingItemDidChange`：歌曲切换，payload 含 track attributes 和 artwork；
- `queueStatus.queueChanged`：队列变化，payload 为 `{ position, total }`；
- `playbackStatus.transitionStarted` / `transitionCompleted`：交叉淡化、无缝或 Automix 转场开始/完成；
- `playbackStatus.playbackTimeDidChange`：播放过程中约每秒一次的时间更新。

`queueChanged` 不包含完整队列项，所以收到它后应重新请求队列；`nowPlayingItemDidChange` 可以作为“提交预加载缓存并立即补齐下一首预加载”的低延迟触发点。官方 CiderDeck 的 Socket.IO 客户端正是监听 `API:Playback` 并把其中的 `type/data` 转发给状态层，且无限重连；其事件常量还列出了上述歌曲切换、队列变化和转场事件。

来源：[Cider API Events 文档](https://taproom.cider.sh/docs/api/events)、[CiderDeck Socket.IO 客户端](https://github.com/ciderapp/CiderDeck/blob/main/src/services/cider-socket.ts#L321-L386)、[CiderDeck 事件常量](https://github.com/ciderapp/CiderDeck/blob/main/src/models/cider-api.ts#L731-L771)。

### 认证与版本

当前 API 使用 `apptoken` header，并按区域要求 token scope；官方概览将 Queue 和 Lyrics 列为独立 scope。CiderDeck 的客户端说明 v2 请求统一走 `/api/v2`，并在 401/403 时区分 token 无效和 scope 不足。旧的 Cider 2.5 Preview API 文档已标明 deprecated，且旧 `/api/v1/lyrics/:id` 在现行 RPC 文档中仍标记为“Currently non-functional”。因此新预加载逻辑应优先使用 v2，同时保留现有 v1 路由作为旧版 Cider 的兼容路径，不应把 v1 文档当作 Cider 4 的完整契约。

来源：[Cider API 概览](https://taproom.cider.sh/docs/api)、[CiderDeck v2 请求与鉴权实现](https://github.com/ciderapp/CiderDeck/blob/main/src/services/cider-client.ts#L899-L1097)、[旧 RPC 文档的弃用说明与 v1 歌词限制](https://github.com/ciderapp/Cider-2/blob/main/docs/Cider%202.5.0%20Preview%20API.md#L197-L204)、[现行 docs 仓库的 v1 歌词说明](https://github.com/ciderapp/docs/blob/main/docs/1.client/rpc.md#L937-L947)。

## Lyrider 当前实现对照

仓库当前已经能拿到大部分预加载所需的“输入”，但只服务当前歌曲：

1. [`CiderService.GetNowPlayingAsync`](../../src/Lyrider/Services/CiderService.cs#L35) 请求 v1 `api/v1/playback/now-playing`；[`GetQueueAsync`](../../src/Lyrider/Services/CiderService.cs#L122) 请求 v1 `api/v1/playback/queue`，解析队列项的 ID、标题、艺术家、专辑、时长和 artwork URL；[`GetLyricsAsync`](../../src/Lyrider/Services/CiderService.cs#L187)按 ID 获取歌词，优先请求 v2 lyrics 再回退 v1。
2. [`MainWindow.RefreshAsync`](../../src/Lyrider/MainWindow.xaml.cs#L327) 使用 1 秒 `DispatcherTimer`。歌曲改变或强制刷新时才调用 [`RefreshTrackDetailsAsync`](../../src/Lyrider/MainWindow.xaml.cs#L404)，并行获取当前歌曲队列与 Cider 歌词；没有歌曲切换时，队列仅每 5 次刷新重新读取，即约每 5 秒一次。
3. `RefreshTrackDetailsAsync` 将当前歌曲的歌词交给 [`LyricsService.ResolveAsync`](../../src/Lyrider/Services/LyricsService.cs#L50)。该方法为整次解析设置 12 秒总超时，单个远程 provider 设置 4 秒，并按配置顺序尝试 Cider、NetEase、QQ Music、Musixmatch、LRCLIB（需要翻译时还会触发更多远程工作）。这意味着当前歌曲歌词解析本身不会无限等待，但把同一流程复制到多首队列歌曲会争抢请求预算。
   另外，[`CiderService`](../../src/Lyrider/Services/CiderService.cs#L12-L15) 的共用 `HttpClient.Timeout` 当前固定为 3 秒；这个限制先于 `LyricsService` 的 12 秒外部源预算生效。若直接复用现有 `GetLyricsAsync` 做预加载，Cider v2 的歌词冷请求会在 3 秒后被当作空结果，因此实现时需要给预加载歌词独立的、可取消的请求预算，而不能简单把所有 Cider 请求的全局超时一起调长。
4. [`ApplyQueue`](../../src/Lyrider/MainWindow.xaml.cs#L644) 将队列项的封面 URL 用 `NormalizeArtworkUrl(..., 160)` 后赋给队列列表；当前主图 [`UpdateNowPlaying`](../../src/Lyrider/MainWindow.xaml.cs#L501) 则使用默认 800px。预加载设计应至少区分“队列缩略图缓存”和“主界面封面缓存”，或缓存原始模板 URL 后按尺寸派生，避免下一首切换时只能得到 160px 图片。
   WinUI 可以在多个 `BitmapImage`/`Image` 间复用同一 resolved URI 的内部图像数据与渲染资源，但这里的 160px 与 800px URL 不同，不能依赖该机制完成主图预热。来源：[Microsoft Learn：Optimize animations, media, and images for WinUI apps](https://learn.microsoft.com/windows/apps/develop/performance/optimize-animations-and-media)。
5. 当前 [`SetArtwork`](../../src/Lyrider/MainWindow.xaml.cs#L628) 只是给 `BitmapImage` 一个 URI，并把同一 source 赋给主图和当前队列图，没有明确的下载完成通知或可复用的图片字节缓存；所以现状是“显示时开始加载”，不是“提前加载完成”。

## 需要特别处理的队列语义

当前 Lyrider 使用的 v1 Cider 队列文档说明，`GET /api/v1/playback/queue` 返回结果还包含部分播放历史和当前歌曲；官方还提醒 Up Next 可见索引可能从大于 1 的数字开始。[旧 RPC 队列说明](https://github.com/ciderapp/docs/blob/main/docs/1.client/rpc.md#L458-L468)和[索引说明](https://github.com/ciderapp/docs/blob/main/docs/1.client/rpc.md#L718-L742)对此有明确描述。

这会带来两个实现风险：

- 不能把响应数组简单当作“当前项之后的纯队列”；应依据服务端 position/currentIndex，并对旧版响应做实测验证。
- 相同歌曲可能在队列中出现多次。当前 [`GetQueueAsync`](../../src/Lyrider/Services/CiderService.cs#L151-L170)在服务端没有 current index 时，会按当前 track ID 找到**第一个**匹配项；重复歌曲会使 currentIndex 推断不可靠，进而错误地过滤或预加载候选。预加载缓存键应使用队列实例/位置（例如 position + queue index），歌曲 ID只作为资源缓存键，不能独立标识一次排队实例。

## 推荐实现方向（仅调查结论，不是本次改动）

### 第一阶段：低风险 MVP

1. 每次成功获取队列时，保留前 1 首（可配置为 1–3 首）候选的完整歌曲资料；优先读取 v2 `attributes`，旧版则沿用当前 v1 解析器。
2. 把候选的歌曲信息写入有上限的内存缓存，以 `track ID + 规格化字段`为资源键，并以队列位置/实例关联候选；队列变化时丢弃不再存在的实例。
3. 对候选 artwork URL 提前创建/下载指定尺寸的图片缓存。队列缩略图 160px 与主图 800px 分开准备，避免把低分辨率封面升级放大。
4. 对下一首先调用 Cider v2 `GET /api/v2/lyrics/:id`，成功结果直接缓存为 `LyricsSnapshot`；冷请求超时或 404 时静默降级，当前歌曲仍按原有流程获取。
   预加载应使用独立 `HttpClient`，或把共用客户端改为无限全局超时并为不同 API 调用分别建立 linked cancellation timeout；后者改动面更大。不要直接提高当前共用客户端的 3 秒超时，否则 Cider 离线时会拖慢 now-playing、队列和控制请求的失败反馈。

### 第二阶段：事件与外部源

1. 增加 Socket.IO `API:Playback` 监听：`nowPlayingItemDidChange` 触发缓存命中/当前歌曲切换，`queueStatus.queueChanged` 触发队列重读和候选重排，连接断开时回退 1 秒轮询。
2. 预加载任务使用独立 cancellation token、并发上限（建议同时最多 1–2 首）和较短的单任务预算；不能复用当前歌曲的 `_lyricsRefreshCancellation`，否则切歌时会把正在完成的下一首预加载一起取消。
3. 只有在用户启用外部歌词源/翻译时才预取外部 provider；为队列项构造搜索所需的标题、艺术家、专辑和时长，并复用现有匹配与 fallback 逻辑。由于 `ResolveAsync` 可能遍历多个外部源且总预算 12 秒，外部预加载应低优先级，并让当前歌曲请求优先。

### 验证重点

- Cider 4 token 是否实际获得 Queue、Lyrics scope；scope 不足时确认 UI 仍能显示当前歌曲和当前队列的可用信息。
- 空队列、自动播放、队列含历史项、队列首项/当前项位置、同一歌曲重复出现、用户在预加载期间手动跳歌。
- 封面 URL 含 `{w}`、`{h}`、`{f}`模板和 CDN 请求失败时的回退。
- 断开 WebSocket、Cider 旧版只提供 v1、歌词 404/504、外部 provider 超时，均不得阻塞播放刷新。

## 最终判断

从接口能力看，预加载歌曲信息和封面是高可行、低风险的改进；Cider 歌词按 ID预加载也是可行的，但应接受冷请求延迟和 scope/失败问题；外部歌词与翻译预加载收益不稳定、请求成本更高，建议后置。最合适的触发架构是“队列查询提供候选 + Socket.IO 事件提前触发 + 本地有界缓存 + 当前歌曲请求兜底”，而不是单纯把现有 1 秒轮询频率调高。
