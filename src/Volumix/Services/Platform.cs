using System;

namespace Volumix.Services;

/// <summary>
/// Чем Windows 10 отличается для Volumix. Панель задач Windows 10 — старое окно Explorer, а не XAML: прозрачное
/// окно WPF, вшитое в неё как дочернее, там не поддерживается, поэтому островок встаёт поверх панели.
/// Сборка для Windows 10 (build.ps1 -Win10, константа WIN10) включает этот режим и на Windows 11 — так его можно проверить.
/// </summary>
internal static class Platform
{
#if WIN10
    public const bool Win10Build = true;
#else
    public const bool Win10Build = false;
#endif

    /// <summary>Windows 11 начинается со сборки 22000.</summary>
    public static readonly bool IsWindows11 = Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Островок можно вшить в панель задач как дочернее окно.</summary>
    public static bool CanEmbedInTaskbar => IsWindows11 && !Win10Build;
}
