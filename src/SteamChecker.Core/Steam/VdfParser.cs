using System.Text;

namespace SteamChecker.Core.Steam;

/// <summary>
/// Valve KeyValues (VDF) テキスト形式のパーサ。
///
/// 対応する構文:
///   "key"  "value"          値ペア
///   "key" { ... }           オブジェクト
///   key    value            引用符なしトークン（Steam が実際に出力する場合がある）
///   // comment              行コメント
///   \\ \" \n \t \r          エスケープ
///   [$WIN32] 等の条件付きトークン → 無視して直前のペアを採用する
///
/// バイナリ VDF (appinfo.vdf) には非対応。本ツールが読むのは
/// libraryfolders.vdf / appmanifest_*.acf / localconfig.vdf のみで、いずれもテキスト形式。
/// </summary>
public static class VdfParser
{
    public static VdfNode Parse(string text)
    {
        var reader = new Reader(text);
        var root = VdfNode.NewObject();
        ParseInto(root, reader, depth: 0);
        return root;
    }

    public static VdfNode ParseFile(string path)
        => Parse(File.ReadAllText(path, DetectEncoding(path)));

    /// <summary>
    /// Steam の VDF は基本 UTF-8 だが、古い localconfig.vdf に BOM や
    /// Latin-1 混じりのものが存在する。BOM があればそれを尊重し、無ければ UTF-8 とみなす。
    /// </summary>
    private static Encoding DetectEncoding(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> bom = stackalloc byte[3];
        var read = fs.Read(bom);
        if (read == 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        }

        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;
        if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) return Encoding.BigEndianUnicode;

        // 不正なバイト列で例外を出さず、置換文字にフォールバックさせる
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    }

    private const int MaxDepth = 64;

    private static void ParseInto(VdfNode parent, Reader reader, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new VdfParseException($"ネストが深すぎます (>{MaxDepth})。破損したファイルの可能性があります。");
        }

        while (true)
        {
            reader.SkipTrivia();
            if (reader.Eof) return;

            if (reader.Peek() == '}')
            {
                reader.Advance();
                return;
            }

            var key = reader.ReadToken();
            if (key is null) return;

            reader.SkipTrivia();
            if (reader.Eof)
            {
                // キーだけで終端 — 破損ファイルだが、読めたところまでは活かす
                return;
            }

            if (reader.Peek() == '{')
            {
                reader.Advance();
                var child = VdfNode.NewObject();
                ParseInto(child, reader, depth + 1);
                parent.SetChild(key, child);
                continue;
            }

            var value = reader.ReadToken();
            if (value is null) return;

            // 条件付きトークン ("key" "value" [$WIN32]) を読み飛ばす
            reader.SkipConditional();

            parent.SetChild(key, VdfNode.NewValue(value));
        }
    }

    private sealed class Reader(string text)
    {
        private readonly string _text = text;
        private int _pos;

        public bool Eof => _pos >= _text.Length;

        public char Peek() => _text[_pos];

        public void Advance() => _pos++;

        public void SkipTrivia()
        {
            while (_pos < _text.Length)
            {
                var c = _text[_pos];
                if (char.IsWhiteSpace(c))
                {
                    _pos++;
                }
                else if (c == '/' && _pos + 1 < _text.Length && _text[_pos + 1] == '/')
                {
                    while (_pos < _text.Length && _text[_pos] != '\n') _pos++;
                }
                else
                {
                    return;
                }
            }
        }

        /// <summary>[$WIN32] のような条件付き修飾子を読み飛ばす。</summary>
        public void SkipConditional()
        {
            var save = _pos;
            SkipTrivia();
            if (_pos < _text.Length && _text[_pos] == '[')
            {
                while (_pos < _text.Length && _text[_pos] != ']') _pos++;
                if (_pos < _text.Length) _pos++; // ']'
                return;
            }

            _pos = save;
        }

        public string? ReadToken()
        {
            SkipTrivia();
            if (Eof) return null;

            return _text[_pos] == '"' ? ReadQuoted() : ReadBare();
        }

        private string ReadQuoted()
        {
            _pos++; // 開き "
            var sb = new StringBuilder();

            while (_pos < _text.Length)
            {
                var c = _text[_pos];

                if (c == '\\' && _pos + 1 < _text.Length)
                {
                    _pos++;
                    var esc = _text[_pos];
                    sb.Append(esc switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        '\\' => '\\',
                        '"' => '"',
                        _ => esc,
                    });
                    _pos++;
                    continue;
                }

                if (c == '"')
                {
                    _pos++; // 閉じ "
                    return sb.ToString();
                }

                sb.Append(c);
                _pos++;
            }

            // 閉じ引用符なしで終端。読めたところまで返す
            return sb.ToString();
        }

        private string ReadBare()
        {
            var start = _pos;
            while (_pos < _text.Length)
            {
                var c = _text[_pos];
                if (char.IsWhiteSpace(c) || c == '{' || c == '}' || c == '"') break;
                _pos++;
            }

            return _text[start.._pos];
        }
    }
}

public sealed class VdfParseException(string message) : Exception(message);
