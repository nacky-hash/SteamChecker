using System.IO.Compression;

namespace SteamChecker.Core.Analysis;

/// <summary>データ片の圧縮しやすさを測る。</summary>
public interface ICompressibilityProbe
{
    /// <summary>圧縮後サイズ / 元サイズ を返す（0.0〜1.0 超もありうる）。</summary>
    double Measure(ReadOnlySpan<byte> data);
}

/// <summary>
/// WOF LZX の挙動を模した読み取り専用プローブ。
///
/// 設計上の要点:
///   WOF の LZX 圧縮は 32 KiB のチャンク単位で独立に圧縮する。
///   つまり実効的な参照窓は 32 KiB しかない。
///   したがって「ファイル全体をまとめて圧縮した比率」で見積もると
///   実際より良い数字が出てしまい、ユーザーに過大な期待を持たせる。
///
///   そこでこのプローブは、サンプルを 32 KiB ごとに切って個別に圧縮し、
///   その合計で比率を出す。WOF の実挙動に構造的に一致させている。
///
/// ユーザーのファイルには一切書き込まない。読むだけ。
/// 「圧縮してみないと分からない」を「読むだけで分かる」に変えるのが本ツールの差別化点。
/// </summary>
public sealed class ChunkedBrotliProbe(
    int chunkSize = 32 * 1024,
    int quality = 9,
    double calibration = 1.0) : ICompressibilityProbe
{
    private readonly int _chunkSize = chunkSize;
    private readonly int _quality = Math.Clamp(quality, 0, 11);

    /// <summary>
    /// Brotli と LZX の差を補正する係数。1.0 = 補正なし。
    ///
    /// 【検証済み 2026-07-29 / Windows 11 実機】
    ///   実ゲーム4種を compact.exe /EXE:LZX で圧縮した実測値と突き合わせた結果、
    ///   最小二乗で求めた補正係数は k = 1.0001 だった。つまり補正は不要。
    ///   残差は平均 2.1pt / 最大 3.1pt で、系統バイアスではなくコンテンツ種別のばらつき。
    ///
    ///   実測 LZX → プローブ推定:
    ///     .dll/.json/.xml  36.4% → 38.2%
    ///     .bundle(Unity)   84.2% → 86.0%
    ///     .webm            97.8% → 96.3%
    ///     .arc(独自)       29.4% → 26.3%
    ///
    ///   詳細は docs/RESEARCH.md を参照。
    /// </summary>
    private readonly double _calibration = calibration;

    /// <summary>
    /// 参照窓はチャンクサイズに合わせる。チャンクより大きい窓を与えても
    /// 参照できるデータが存在しないので無意味であり、
    /// 逆に小さいと WOF より悪い数字を出してしまう。
    /// Brotli の指定可能範囲は 10〜24。
    /// </summary>
    private readonly int _window = Math.Clamp(
        (int)Math.Ceiling(Math.Log2(Math.Max(chunkSize, 1024))), 10, 24);

    public double Measure(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return 1.0;

        long compressed = 0;
        var offset = 0;

        // チャンク境界で使い回すバッファ
        var maxOut = BrotliEncoder.GetMaxCompressedLength(_chunkSize);
        var outBuffer = new byte[maxOut];

        while (offset < data.Length)
        {
            var length = Math.Min(_chunkSize, data.Length - offset);
            var chunk = data.Slice(offset, length);

            if (BrotliEncoder.TryCompress(chunk, outBuffer, out var written, _quality, _window))
            {
                // WOF は「縮まないチャンクは非圧縮で格納」する。実挙動に合わせて下駄を履かせない
                compressed += Math.Min(written, length);
            }
            else
            {
                compressed += length;
            }

            offset += length;
        }

        var ratio = (double)compressed / data.Length * _calibration;
        return Math.Clamp(ratio, 0.0, 1.0);
    }
}
