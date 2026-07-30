using System.Diagnostics.CodeAnalysis;

namespace SteamChecker.Core.Steam;

/// <summary>
/// Valve KeyValues (VDF) テキスト形式のノード。
/// 値ノード (<see cref="Value"/> が non-null) か、子を持つオブジェクトノードのどちらか。
/// キーの比較は Steam の実装に合わせて大文字小文字を区別しない。
/// </summary>
public sealed class VdfNode
{
    private readonly Dictionary<string, VdfNode> _children =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>値ノードの場合の文字列値。オブジェクトノードでは null。</summary>
    public string? Value { get; init; }

    /// <summary>子ノード。値ノードでは空。</summary>
    public IReadOnlyDictionary<string, VdfNode> Children => _children;

    public bool IsValue => Value is not null;

    public static VdfNode NewObject() => new();

    public static VdfNode NewValue(string value) => new() { Value = value };

    internal void SetChild(string key, VdfNode node)
    {
        // Steam の VDF は同一キーの重複を許す。後勝ちで上書きする
        // （ただし双方がオブジェクトなら子をマージする。libraryfolders.vdf 等の実例に合わせる）
        if (_children.TryGetValue(key, out var existing) && !existing.IsValue && !node.IsValue)
        {
            foreach (var (k, v) in node._children)
            {
                existing.SetChild(k, v);
            }
            return;
        }

        _children[key] = node;
    }

    /// <summary>パスを辿って子ノードを取得する。見つからなければ null。</summary>
    public VdfNode? Find(params string[] path)
    {
        var current = this;
        foreach (var segment in path)
        {
            if (current is null || !current._children.TryGetValue(segment, out current))
            {
                return null;
            }
        }

        return current;
    }

    /// <summary>パスを辿って文字列値を取得する。値ノードでなければ null。</summary>
    public string? GetString(params string[] path) => Find(path)?.Value;

    public bool TryGetInt64(out long result, params string[] path)
    {
        result = 0;
        var raw = GetString(path);
        return raw is not null && long.TryParse(raw, out result);
    }

    public long GetInt64OrDefault(long fallback, params string[] path)
        => TryGetInt64(out var v, path) ? v : fallback;

    /// <summary>キーが数値の子ノードを列挙する。appmanifest の apps ブロック等で使う。</summary>
    public IEnumerable<(long Key, VdfNode Node)> NumericChildren()
    {
        foreach (var (key, node) in _children)
        {
            if (long.TryParse(key, out var numeric))
            {
                yield return (numeric, node);
            }
        }
    }

    public bool TryGetChild(string key, [NotNullWhen(true)] out VdfNode? node)
        => _children.TryGetValue(key, out node);
}
