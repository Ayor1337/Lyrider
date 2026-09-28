# 封面预加载集成测试

此项目直接运行真实 WinUI `BitmapImage`、`LoadedImageSurface` 和生产 `ArtworkPresenter`，使用本地 HTTP 服务器提供仓库中的图片，不需要 Cider、Token 或外网。测试窗口不显示，结果写入构建目录的 `results.log`。

在 Windows 上从仓库根目录运行：

```powershell
.\scripts\test-artwork.ps1
```

已构建且代码未变动时，可加 `-NoBuild`，约几秒完成。脚本失败时抛出错误。此项目独立于 `Lyrider.sln` 和普通 `net10.0` 单元测试；使用自包含 Windows App SDK，避免安装运行时。

主要回归断言：调用 `PrepareNext` 后、调用 `Show` 前，前景封面已经解码，切歌时复用前景和背景对象，且不新增 HTTP 请求。旧实现仅创建带 URI 的 `BitmapImage`，该断言会因切歌前 `PixelWidth == 0` 失败。

同时检查进行中的预加载被切歌接管、后续队列刷新、队列替换取消、失败回退、清空和释放。失败回退验证前景 URI 设置及背景资源重建；不保证失败 URL 能立即恢复（WinUI 可能缓存失败结果），也不验证屏幕上的渲染效果。
