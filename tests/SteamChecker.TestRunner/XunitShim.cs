// ---------------------------------------------------------------------------
// xUnit 互換の最小シム。
//
// なぜこれが要るのか:
//   本プロジェクトの正式なテストプロジェクトは xUnit を使う（Windows 側の
//   通常の開発環境ではそれが最も素直）。ただし NuGet に到達できない環境でも
//   同じテストコードをそのまま走らせて検証できるようにしておきたい。
//
//   そこで、テストの .cs ファイルは 1 か所にだけ置き、
//     - SteamChecker.Tests      → 本物の xUnit で実行
//     - SteamChecker.TestRunner → このシム + 自前ランナーで実行
//   の 2 通りからリンクする構成にしている。テストの二重管理は発生しない。
// ---------------------------------------------------------------------------

using System.Collections;

namespace Xunit;

[AttributeUsage(AttributeTargets.Method)]
public sealed class FactAttribute : Attribute
{
    public string? Skip { get; set; }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class TheoryAttribute : Attribute
{
    public string? Skip { get; set; }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class InlineDataAttribute(params object?[] data) : Attribute
{
    public object?[] Data { get; } = data;
}

public sealed class XunitAssertException(string message) : Exception(message);

public static class Assert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition) throw new XunitAssertException(message ?? "Assert.True: 条件が false でした");
    }

    public static void False(bool condition, string? message = null)
    {
        if (condition) throw new XunitAssertException(message ?? "Assert.False: 条件が true でした");
    }

    public static void Equal<T>(T expected, T actual)
    {
        // 本物の xUnit はコレクションを要素単位で比較する。
        // EqualityComparer<List<T>>.Default は参照比較なので、ここで揃えておかないと
        // シム経由のときだけ落ちる（＝テストが環境依存になる）
        if (expected is IEnumerable expectedSeq and not string &&
            actual is IEnumerable actualSeq and not string)
        {
            var a = expectedSeq.Cast<object?>().ToList();
            var b = actualSeq.Cast<object?>().ToList();

            if (a.Count == b.Count && a.Zip(b).All(p => Equals(p.First, p.Second))) return;

            throw new XunitAssertException($"Assert.Equal: 期待 <{Format(expected)}> / 実際 <{Format(actual)}>");
        }

        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new XunitAssertException($"Assert.Equal: 期待 <{Format(expected)}> / 実際 <{Format(actual)}>");
        }
    }

    public static void Equal(double expected, double actual, int precision)
    {
        if (Math.Abs(expected - actual) > Math.Pow(10, -precision))
        {
            throw new XunitAssertException(
                $"Assert.Equal(precision {precision}): 期待 <{expected}> / 実際 <{actual}>");
        }
    }

    public static void NotEqual<T>(T expected, T actual)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new XunitAssertException($"Assert.NotEqual: 両方とも <{Format(actual)}> でした");
        }
    }

    public static void Null(object? value)
    {
        if (value is not null) throw new XunitAssertException($"Assert.Null: <{Format(value)}> でした");
    }

    public static void NotNull(object? value)
    {
        if (value is null) throw new XunitAssertException("Assert.NotNull: null でした");
    }

    public static T Single<T>(IEnumerable<T> collection)
    {
        var list = collection.ToList();
        if (list.Count != 1)
        {
            throw new XunitAssertException($"Assert.Single: 要素数が {list.Count} でした");
        }

        return list[0];
    }

    public static void Empty(IEnumerable collection)
    {
        var count = collection.Cast<object?>().Count();
        if (count != 0) throw new XunitAssertException($"Assert.Empty: 要素数が {count} でした");
    }

    public static void NotEmpty(IEnumerable collection)
    {
        if (!collection.Cast<object?>().Any()) throw new XunitAssertException("Assert.NotEmpty: 空でした");
    }

    public static void Contains(string expectedSubstring, string actualString)
    {
        if (actualString is null || !actualString.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new XunitAssertException($"Assert.Contains: \"{expectedSubstring}\" が \"{actualString}\" に含まれません");
        }
    }

    public static void DoesNotContain(string expectedSubstring, string actualString)
    {
        if (actualString is not null && actualString.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new XunitAssertException($"Assert.DoesNotContain: \"{expectedSubstring}\" が \"{actualString}\" に含まれています");
        }
    }

    public static void Contains<T>(IEnumerable<T> collection, Func<T, bool> predicate)
    {
        if (!collection.Any(predicate)) throw new XunitAssertException("Assert.Contains: 該当要素がありません");
    }

    public static void Contains<T>(T expected, IEnumerable<T> collection)
    {
        if (!collection.Contains(expected))
        {
            throw new XunitAssertException($"Assert.Contains: <{Format(expected)}> が見つかりません");
        }
    }

    public static void DoesNotContain<T>(IEnumerable<T> collection, Func<T, bool> predicate)
    {
        if (collection.Any(predicate)) throw new XunitAssertException("Assert.DoesNotContain: 該当要素が存在します");
    }

    public static void DoesNotContain<T>(T expected, IEnumerable<T> collection)
    {
        if (collection.Contains(expected))
        {
            throw new XunitAssertException($"Assert.DoesNotContain: <{Format(expected)}> が存在します");
        }
    }

    public static void InRange<T>(T actual, T low, T high) where T : IComparable<T>
    {
        if (actual.CompareTo(low) < 0 || actual.CompareTo(high) > 0)
        {
            throw new XunitAssertException(
                $"Assert.InRange: <{Format(actual)}> が [{Format(low)}, {Format(high)}] の外です");
        }
    }

    public static T Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            throw new XunitAssertException(
                $"Assert.Throws<{typeof(T).Name}>: 実際は {ex.GetType().Name} が投げられました");
        }

        throw new XunitAssertException($"Assert.Throws<{typeof(T).Name}>: 例外が投げられませんでした");
    }

    public static T IsType<T>(object? value)
    {
        if (value is not T typed)
        {
            throw new XunitAssertException(
                $"Assert.IsType<{typeof(T).Name}>: 実際は {value?.GetType().Name ?? "null"} でした");
        }

        return typed;
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        IEnumerable e and not string => "[" + string.Join(", ", e.Cast<object?>().Take(8).Select(Format)) + "]",
        _ => value.ToString() ?? "null",
    };
}
