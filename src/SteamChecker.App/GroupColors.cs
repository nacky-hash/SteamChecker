using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using SteamChecker.Core.Presentation;

namespace SteamChecker.App;

/// <summary>
/// 見出しの文字列 → Brush。
/// 配色そのものは Core の <see cref="AdviceColors"/> にある（分類の網羅をテストで保証するため）。
/// ここは色コードを WPF の Brush に変えるだけにしておく。
/// </summary>
internal static class BrushCache
{
    private static readonly Dictionary<string, Brush> Cache = [];

    public static Brush Get(string hex)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(hex, out var cached)) return cached;

            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            Cache[hex] = brush;
            return brush;
        }
    }
}

public sealed class GroupColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BrushCache.Get(AdviceColors.ByLabel(value as string).Background);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class GroupAccentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BrushCache.Get(AdviceColors.ByLabel(value as string).Accent);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
