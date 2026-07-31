using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using SteamChecker.Core.Analysis;
using SteamChecker.Core.Presentation;

namespace SteamChecker.App;

/// <summary>
/// 分類の見出しに使う色。
///
/// 方針:
///   - 色だけに意味を持たせない。見出しの文字が主で、色は「探しやすさ」の補助
///   - 「やっていい（緑）／条件つき（青・橙）／やらなくていい（灰）／やるな（赤）」の 4 段階
///   - 緑と赤は明度も変えてある（色覚の型によっては色相差が出ないため）
/// </summary>
public static class GroupColors
{
    private static readonly Dictionary<string, (string Background, string Accent)> Map = new()
    {
        [AdviceFormatter.Label(AdviceKind.Compress)] = ("#E7F6EC", "#2E9E52"),
        [AdviceFormatter.Label(AdviceKind.CompressUpdatesOften)] = ("#E8F1FB", "#2E6FB8"),
        [AdviceFormatter.Label(AdviceKind.CompressAntiCheat)] = ("#FDF3E3", "#C7841A"),
        [AdviceFormatter.Label(AdviceKind.NotWorthCompressing)] = ("#F2F2F2", "#8A8A8A"),
        [AdviceFormatter.Label(AdviceKind.DoNotCompress)] = ("#FBEAEA", "#B33A3A"),
        [AdviceFormatter.Label(AdviceKind.AlreadyCompressed)] = ("#EDF0F4", "#5B6B80"),
    };

    private const string DefaultBackground = "#F2F2F2";
    private const string DefaultAccent = "#8A8A8A";

    public static Brush Background(string? group) => Brush(group, pickAccent: false);

    public static Brush Accent(string? group) => Brush(group, pickAccent: true);

    private static Brush Brush(string? group, bool pickAccent)
    {
        var hex = group is not null && Map.TryGetValue(group, out var pair)
            ? (pickAccent ? pair.Accent : pair.Background)
            : (pickAccent ? DefaultAccent : DefaultBackground);

        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

public sealed class GroupColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => GroupColors.Background(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class GroupAccentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => GroupColors.Accent(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
