using System.Runtime.InteropServices;

/// <summary>
/// 空きメモリの取得（Windows 専用）。
///
/// Core に Windows 依存を持ち込まない方針（AGENTS.md「アーキテクチャの制約」）のため、
/// P/Invoke はアプリ層のここに置き、Core へはデリゲートとして渡す。
/// レジストリ参照を注入しているのと同じ考え方。
/// </summary>
internal static class SystemMemory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    // 新しい LibraryImport は AllowUnsafeBlocks を要求する。
    // P/Invoke 1 箇所のためにプロジェクト全体で unsafe を許可するのは
    // 割に合わないので、ここは DllImport を使い、その提案だけを抑制する。
#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
#pragma warning restore SYSLIB1054

    /// <summary>
    /// 利用可能な物理メモリ（バイト）。取得できなければ 0 を返す。
    ///
    /// ullAvailPhys は空き＋スタンバイ（解放可能なキャッシュ）を含む。
    /// ファイルキャッシュで見かけの空きが減っただけの状態を
    /// 「メモリ不足」と誤判定しないために、この値を使う。
    /// </summary>
    public static long AvailableBytes()
    {
        if (!OperatingSystem.IsWindows()) return 0;

        var status = new MemoryStatusEx
        {
            dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>(),
        };

        if (!GlobalMemoryStatusEx(ref status)) return 0;

        return status.ullAvailPhys > long.MaxValue
            ? long.MaxValue
            : (long)status.ullAvailPhys;
    }
}
