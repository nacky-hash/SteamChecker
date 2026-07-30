namespace SteamChecker.Core.Steam;

/// <summary>
/// Steam のインストール先を特定する。
///
/// レジストリ参照は Windows 依存なので Core からは直接触らず、
/// アプリ層から <paramref name="registryLookup"/> として注入する。
/// これにより Core は OS 非依存のまま保たれ、Linux 上でもテストできる。
/// </summary>
public sealed class SteamLocator(IFileSystem fs, Func<string?>? registryLookup = null)
{
    private readonly IFileSystem _fs = fs;
    private readonly Func<string?>? _registryLookup = registryLookup;

    /// <summary>レジストリ → 既知のパス の順に探し、最初に見つかったものを返す。</summary>
    public string? Locate()
    {
        var fromRegistry = _registryLookup?.Invoke();
        if (IsSteamRoot(fromRegistry)) return fromRegistry;

        foreach (var candidate in CandidatePaths())
        {
            if (IsSteamRoot(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>指定パスが Steam のルートらしいか（steamapps があるか）。</summary>
    public bool IsSteamRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return _fs.DirectoryExists(_fs.Combine(path, "steamapps"));
    }

    private IEnumerable<string> CandidatePaths()
    {
        if (OperatingSystem.IsWindows())
        {
            var pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            var pf = Environment.GetEnvironmentVariable("ProgramFiles");

            if (!string.IsNullOrEmpty(pf86)) yield return _fs.Combine(pf86, "Steam");
            if (!string.IsNullOrEmpty(pf)) yield return _fs.Combine(pf, "Steam");

            foreach (var drive in "CDEFGH")
            {
                yield return $@"{drive}:\Steam";
                yield return $@"{drive}:\Program Files (x86)\Steam";
                yield return $@"{drive}:\SteamLibrary";
            }
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) yield break;

            yield return _fs.Combine(home, ".steam", "steam");
            yield return _fs.Combine(home, ".local", "share", "Steam");
            yield return _fs.Combine(home, "Library", "Application Support", "Steam");
        }
    }
}
