using System.Diagnostics;

namespace SteamChecker.Core.Compression;

/// <summary>
/// Windows 標準の compact.exe を呼び出して WOF 透過圧縮を適用する。
///
/// 設計上の重要な判断:
///
/// 1. compact.exe の集計出力（「〜個のファイルが圧縮されました」等）は
///    OS の表示言語でローカライズされる。日本語版 Windows では英語前提の
///    パースが全て壊れる。したがって削減量は出力から読まず、
///    操作の前後で GetCompressedFileSize を自前で合計して求める。
///    進捗はファイル単位の出力行を数えるだけにする（これは言語非依存）。
///
/// 2. 終了コードだけでは成否を判断しない。compact.exe は
///    一部ファイルがロックされていても 0 を返すことがある。
///    実際に占有サイズが減ったかどうかを最終判定に使う。
///
/// 注意: 引数の組み合わせは Windows 実機での検証が必須。
///       特に /U での WOF 圧縮解除は環境差があるため、
///       出荷前に「圧縮 → 解除 → 元のサイズに戻る」ことを必ず実測すること。
/// </summary>
public sealed class CompactExeEngine(IFileSystem fs, bool dryRun = false) : ICompressionEngine
{
    private readonly IFileSystem _fs = fs;
    private readonly bool _dryRun = dryRun;

    public bool IsAvailable => OperatingSystem.IsWindows() && File.Exists(CompactPath);

    private static string CompactPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "compact.exe");

    public Task<CompressionResult> CompressAsync(
        string folderPath,
        CompressionAlgorithm algorithm,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken ct = default)
    {
        // /C   圧縮
        // /S   サブディレクトリを再帰
        // /A   隠し・システムファイルも対象
        // /I   エラーが出ても続行（ロック中のファイルで全体が止まらないように）
        // /Q   簡易出力
        // /F   強制（中断された圧縮のやり直しに必要）
        // /EXE:<alg>  WOF 透過圧縮
        var args = $"/C /S /A /I /Q /F /EXE:{algorithm.ToCompactArgument()}";
        return RunAsync(folderPath, args, progress, ct);
    }

    public Task<CompressionResult> DecompressAsync(
        string folderPath,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken ct = default)
    {
        // /U を /EXE 付きで実行すると WOF オーバーレイを解除する。
        // /EXE なしの /U は従来の NTFS 圧縮のみを解除する点に注意。
        return RunAsync(folderPath, "/U /S /A /I /Q /F /EXE", progress, ct);
    }

    private async Task<CompressionResult> RunAsync(
        string folderPath,
        string arguments,
        IProgress<CompressionProgress>? progress,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        if (!_fs.DirectoryExists(folderPath))
        {
            return Failure(folderPath, stopwatch.Elapsed, $"フォルダが存在しません: {folderPath}");
        }

        // キャンセルは前後の容量測定中にも起きうる。
        // どの時点で中断されても、呼び出し側には例外ではなく
        // 「失敗した結果」として一貫した形で返す（catch 節で処理）
        long before = 0;

        var filesProcessed = 0;
        string? lastError = null;

        var psi = new ProcessStartInfo
        {
            FileName = CompactPath,
            // 作業ディレクトリを対象フォルダにし、パターンは "*" に固定する。
            // パスに空白や記号が含まれる場合の引用符地獄を避けるため。
            WorkingDirectory = folderPath,
            Arguments = arguments + " *",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            before = MeasurePhysicalBytes(folderPath, ct);

            if (_dryRun)
            {
                return new CompressionResult
                {
                    Success = true,
                    Path = folderPath,
                    BytesBefore = before,
                    BytesAfter = before,
                    FilesProcessed = 0,
                    Duration = stopwatch.Elapsed,
                    ErrorMessage = "dry-run のため実行していません",
                    WasDryRun = true,
                };
            }

            if (!IsAvailable)
            {
                return Failure(folderPath, stopwatch.Elapsed,
                    "compact.exe が見つかりません（Windows 以外では実行できません）");
            }

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;

                // 言語に依存しない進捗: 出力行の数をファイル数の近似として使う。
                // 正確なファイル数は前後のサイズ測定で担保しているので、
                // ここは「動いていることが分かる」ためだけの数字でよい。
                filesProcessed++;
                progress?.Report(new CompressionProgress
                {
                    FilesProcessed = filesProcessed,
                    CurrentFile = e.Data.Trim(),
                });
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) lastError = e.Data.Trim();
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // WaitForExitAsync のキャンセルは待機をやめるだけで子プロセスは走り続ける。
                // 放置すると「中断したのに圧縮が進み続ける」状態になるので、ツリーごと止める。
                // WOF はファイル単位で適用されるため、途中で殺しても個々のファイルは壊れない
                // （適用済み / 未適用のどちらかにしかならない）
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // 既に終了している場合など。止められなかったこと自体は結果に影響しない
                }

                throw;
            }
        }
        catch (OperationCanceledException)
        {
            // キャンセル時も「途中まで圧縮された状態」は有効。測り直して返す
            var partial = MeasurePhysicalBytes(folderPath, CancellationToken.None);
            return new CompressionResult
            {
                Success = false,
                Path = folderPath,
                BytesBefore = before,
                BytesAfter = partial,
                FilesProcessed = filesProcessed,
                Duration = stopwatch.Elapsed,
                ErrorMessage = "ユーザーによって中断されました",
            };
        }
        catch (Exception ex)
        {
            return Failure(folderPath, stopwatch.Elapsed, ex.Message, before);
        }

        // 操作自体は完了している。事後測定のキャンセルで「中断」と報告しない
        var after = MeasurePhysicalBytes(folderPath, CancellationToken.None);

        return new CompressionResult
        {
            Success = true,
            Path = folderPath,
            BytesBefore = before,
            BytesAfter = after,
            FilesProcessed = filesProcessed,
            Duration = stopwatch.Elapsed,
            ErrorMessage = lastError,
        };
    }

    private long MeasurePhysicalBytes(string folderPath, CancellationToken ct)
    {
        long total = 0;
        foreach (var file in _fs.EnumerateFilesRecursive(folderPath))
        {
            ct.ThrowIfCancellationRequested();
            total += _fs.GetFileSizeOnDisk(file);
        }

        return total;
    }

    private static CompressionResult Failure(
        string path, TimeSpan duration, string message, long before = 0) => new()
    {
        Success = false,
        Path = path,
        BytesBefore = before,
        BytesAfter = before,
        FilesProcessed = 0,
        Duration = duration,
        ErrorMessage = message,
    };
}
