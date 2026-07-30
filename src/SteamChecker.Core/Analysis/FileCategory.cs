namespace SteamChecker.Core.Analysis;

/// <summary>圧縮の効き方でファイルを分類したカテゴリ。</summary>
public enum FileCategory
{
    /// <summary>実行ファイル・ライブラリ。LZX が最もよく効く。</summary>
    Executable,

    /// <summary>テキスト・スクリプト・設定。極めてよく効く。</summary>
    Text,

    /// <summary>非圧縮アセット（生テクスチャ、PCM 音声、Unreal の loose uasset 等）。よく効く。</summary>
    RawAsset,

    /// <summary>ブロック圧縮テクスチャ (.dds 等)。ほとんど効かない。</summary>
    BlockCompressedTexture,

    /// <summary>圧縮済みメディア（動画・音声・画像）。効かない。</summary>
    CompressedMedia,

    /// <summary>
    /// 圧縮フォーマットそのもの (.zip / .7z / .gz 等)。定義上すでに圧縮済みなので効かない。
    /// このカテゴリだけは実測を省いてよい。
    /// </summary>
    CompressionFormat,

    /// <summary>
    /// ゲームの独自コンテナ (.arc / .rpf / .vpk / .pck 等)。
    /// 名前がアーカイブでも、中身が圧縮済みとは限らない。**必ず実測すること。**
    ///
    /// 実例: theHunter: Call of the Wild の .arc は実測 LZX 29.4%（117GB → 約82GB 削減）。
    /// これを「アーカイブだから効かない」と決め打つと 34 倍過小に見積もる。
    /// </summary>
    GameArchive,

    /// <summary>正体不明。実測しないと分からない。</summary>
    Unknown,
}

public static class FileCategories
{
    private static readonly Dictionary<string, FileCategory> Map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // --- 実行ファイル系 ---
            [".exe"] = FileCategory.Executable,
            [".dll"] = FileCategory.Executable,
            [".sys"] = FileCategory.Executable,
            [".ocx"] = FileCategory.Executable,
            [".pdb"] = FileCategory.Executable,
            [".lib"] = FileCategory.Executable,
            [".obj"] = FileCategory.Executable,
            [".so"] = FileCategory.Executable,
            [".dylib"] = FileCategory.Executable,
            [".node"] = FileCategory.Executable,

            // --- テキスト系 ---
            [".txt"] = FileCategory.Text,
            [".xml"] = FileCategory.Text,
            [".json"] = FileCategory.Text,
            [".ini"] = FileCategory.Text,
            [".cfg"] = FileCategory.Text,
            [".conf"] = FileCategory.Text,
            [".lua"] = FileCategory.Text,
            [".csv"] = FileCategory.Text,
            [".html"] = FileCategory.Text,
            [".htm"] = FileCategory.Text,
            [".js"] = FileCategory.Text,
            [".css"] = FileCategory.Text,
            [".md"] = FileCategory.Text,
            [".yml"] = FileCategory.Text,
            [".yaml"] = FileCategory.Text,
            [".log"] = FileCategory.Text,
            [".shader"] = FileCategory.Text,
            [".hlsl"] = FileCategory.Text,
            [".glsl"] = FileCategory.Text,
            [".fx"] = FileCategory.Text,
            [".po"] = FileCategory.Text,
            [".srt"] = FileCategory.Text,

            // --- 非圧縮アセット ---
            [".uasset"] = FileCategory.RawAsset,
            [".umap"] = FileCategory.RawAsset,
            [".uexp"] = FileCategory.RawAsset,
            [".ubulk"] = FileCategory.RawAsset,
            [".bsa"] = FileCategory.RawAsset,
            [".ba2"] = FileCategory.RawAsset,
            [".esm"] = FileCategory.RawAsset,
            [".esp"] = FileCategory.RawAsset,
            [".wav"] = FileCategory.RawAsset,
            [".aiff"] = FileCategory.RawAsset,
            [".tga"] = FileCategory.RawAsset,
            [".bmp"] = FileCategory.RawAsset,
            [".psd"] = FileCategory.RawAsset,
            [".fbx"] = FileCategory.RawAsset,
            [".obj3d"] = FileCategory.RawAsset,
            [".mesh"] = FileCategory.RawAsset,
            [".anim"] = FileCategory.RawAsset,
            [".ttf"] = FileCategory.RawAsset,
            [".otf"] = FileCategory.RawAsset,

            // --- ブロック圧縮テクスチャ ---
            [".dds"] = FileCategory.BlockCompressedTexture,
            [".ktx"] = FileCategory.BlockCompressedTexture,
            [".ktx2"] = FileCategory.BlockCompressedTexture,
            [".basis"] = FileCategory.BlockCompressedTexture,
            [".astc"] = FileCategory.BlockCompressedTexture,

            // --- 圧縮済みメディア ---
            [".mp4"] = FileCategory.CompressedMedia,
            [".webm"] = FileCategory.CompressedMedia,
            [".mkv"] = FileCategory.CompressedMedia,
            [".avi"] = FileCategory.CompressedMedia,
            [".wmv"] = FileCategory.CompressedMedia,
            [".mov"] = FileCategory.CompressedMedia,
            [".bik"] = FileCategory.CompressedMedia,
            [".bk2"] = FileCategory.CompressedMedia,
            [".usm"] = FileCategory.CompressedMedia,
            [".ivf"] = FileCategory.CompressedMedia,
            [".mp3"] = FileCategory.CompressedMedia,
            [".ogg"] = FileCategory.CompressedMedia,
            [".opus"] = FileCategory.CompressedMedia,
            [".m4a"] = FileCategory.CompressedMedia,
            [".aac"] = FileCategory.CompressedMedia,
            [".flac"] = FileCategory.CompressedMedia,
            [".wem"] = FileCategory.CompressedMedia,
            [".fsb"] = FileCategory.CompressedMedia,
            [".bnk"] = FileCategory.CompressedMedia,
            [".xwb"] = FileCategory.CompressedMedia,
            [".png"] = FileCategory.CompressedMedia,
            [".jpg"] = FileCategory.CompressedMedia,
            [".jpeg"] = FileCategory.CompressedMedia,
            [".webp"] = FileCategory.CompressedMedia,
            [".gif"] = FileCategory.CompressedMedia,

            // --- 圧縮フォーマットそのもの（実測不要） ---
            [".zip"] = FileCategory.CompressionFormat,
            [".7z"] = FileCategory.CompressionFormat,
            [".rar"] = FileCategory.CompressionFormat,
            [".gz"] = FileCategory.CompressionFormat,
            [".bz2"] = FileCategory.CompressionFormat,
            [".xz"] = FileCategory.CompressionFormat,
            [".zst"] = FileCategory.CompressionFormat,
            [".lz4"] = FileCategory.CompressionFormat,

            // --- ゲームの独自コンテナ（必ず実測する） ---
            [".cab"] = FileCategory.GameArchive,
            [".msi"] = FileCategory.GameArchive,
            [".vpk"] = FileCategory.GameArchive,
            [".pck"] = FileCategory.GameArchive,
            [".rpf"] = FileCategory.GameArchive,
            [".forge"] = FileCategory.GameArchive,
            [".arc"] = FileCategory.GameArchive,
        };

    /// <summary>
    /// 拡張子からカテゴリを引く。
    /// 注意: .pak / .dat / .bin / .assets などは中身が圧縮済みかどうかがタイトル依存で、
    /// 拡張子だけでは判断できない。これらは Unknown に落として実測サンプリングに委ねる。
    /// </summary>
    public static FileCategory FromExtension(string extension)
        => Map.TryGetValue(extension, out var c) ? c : FileCategory.Unknown;

    /// <summary>
    /// カテゴリごとの「圧縮後サイズ / 元サイズ」の初期推定値。
    /// あくまでサンプリング前の当たりを付けるための値であり、
    /// 最終的な表示には <see cref="SamplingEstimator"/> の実測値を使うこと。
    /// </summary>
    public static double PriorRatio(FileCategory category) => category switch
    {
        FileCategory.Text => 0.28,
        FileCategory.Executable => 0.45,
        FileCategory.RawAsset => 0.62,
        FileCategory.BlockCompressedTexture => 0.88,
        FileCategory.CompressedMedia => 0.98,
        FileCategory.CompressionFormat => 0.98,
        // GameArchive の事前値は「分からない」に倒す。実測が入れば必ず上書きされる
        FileCategory.GameArchive => 0.80,
        _ => 0.80,
    };
}
