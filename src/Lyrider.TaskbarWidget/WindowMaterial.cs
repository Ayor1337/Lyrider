namespace Lyrider.TaskbarWidget;

/// <summary>
/// 把窗口交给 DWM 绘制系统材质。仅在窗口不是 layered window（<c>AllowsTransparency="False"</c>）时有效。
/// </summary>
internal static class WindowMaterial
{
    /// <summary>
    /// 尝试应用亚克力材质。返回 <see langword="false" /> 表示当前系统不支持
    /// （系统材质要求 Windows 11 Build 22621 及以上），调用方应回退到不透明纯色背景。
    /// </summary>
    internal static bool TryApplyAcrylic(nint windowHandle, bool isLightTheme)
    {
        if (windowHandle == nint.Zero)
        {
            return false;
        }

        // 圆角与深浅色是尽力而为：旧系统上单独失败不应放弃材质本身。
        SetAttribute(windowHandle, NativeMethods.DwmwaUseImmersiveDarkMode, isLightTheme ? 0 : 1);
        SetAttribute(windowHandle, NativeMethods.DwmwaWindowCornerPreference, NativeMethods.DwmcpRound);

        // 负边距表示让 frame 覆盖整个客户区，材质才有地方绘制。
        var margins = new NativeMargins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        NativeMethods.DwmExtendFrameIntoClientArea(windowHandle, ref margins);

        return SetAttribute(windowHandle, NativeMethods.DwmwaSystemBackdropType, NativeMethods.DwmsbtTransientWindow);
    }

    private static bool SetAttribute(nint windowHandle, int attribute, int value) =>
        NativeMethods.DwmSetWindowAttribute(windowHandle, attribute, ref value, sizeof(int)) == 0;
}
