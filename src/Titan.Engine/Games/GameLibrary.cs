using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Titan.Engine.Games;

public enum GameSource { Steam, Epic, GOG, Ubisoft, EA, BattleNet, Riot, Rockstar, Amazon, Xbox, Local, Manual }

public sealed record GameEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required GameSource Source { get; init; }
    public required string InstallDir { get; init; }
    /// <summary>Main executable, best guess. May be null when only a launcher URI is known.</summary>
    public string? Exe { get; init; }
    /// <summary>How to start it: a launcher URI or null (then <see cref="Exe"/> is run).</summary>
    public string? LaunchUri { get; init; }
    public string? LaunchArgs { get; init; }
    public string? CoverPath { get; init; }
    public bool AutoBoost { get; set; }
}

/// <summary>
/// Finds installed games: every launcher we know how to read, plus a heuristic scan of every fixed drive for
/// games that no launcher owns (repacks, torrents, AnkerGames, portable installs).
/// </summary>
public static class GameLibrary
{
    public static IReadOnlyList<GameEntry> Scan(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var found = new List<GameEntry>();
        void Add(IEnumerable<GameEntry> games, string label)
        {
            progress?.Report(label);
            try { found.AddRange(games); } catch { /* one broken launcher never blocks the rest */ }
        }

        Add(SafeList(Steam), "Steam");
        Add(SafeList(Epic), "Epic Games");
        Add(SafeList(Gog), "GOG");
        Add(SafeList(Ubisoft), "Ubisoft Connect");
        Add(SafeList(Xbox), "Xbox / Microsoft Store");
        Add(SafeList(UninstallRegistry), "EA · Battle.net · Riot · Rockstar · Amazon");
        ct.ThrowIfCancellationRequested();

        var known = found.Select(g => Normalize(g.InstallDir)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Add(SafeList(() => LocalScan(known, progress, ct)), "Unidades locales");

        return found
            .Where(g => Directory.Exists(g.InstallDir))
            .GroupBy(g => Normalize(g.InstallDir), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static List<GameEntry> SafeList(Func<IEnumerable<GameEntry>> f)
    {
        try { return f().ToList(); } catch { return []; }
    }

    // ------------------------------------------------------------------ Steam
    private static readonly HashSet<string> SteamTools = ["228980", "1070560", "1391110", "1628350", "1493710", "2180100", "961940", "1113280", "1245040", "250820"];

    private static IEnumerable<GameEntry> Steam()
    {
        string? steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                     ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        if (steam is null) yield break;
        steam = steam.Replace('/', '\\');
        var libraries = new List<string> { steam };
        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));

        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                string text = File.ReadAllText(acf);
                string? id = Vdf(text, "appid"), name = Vdf(text, "name"), dir = Vdf(text, "installdir");
                if (id is null || name is null || dir is null || SteamTools.Contains(id)) continue;
                if (name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase)) continue;
                string install = Path.Combine(apps, "common", dir);
                yield return new GameEntry
                {
                    Id = "steam:" + id, Name = name, Source = GameSource.Steam, InstallDir = install,
                    Exe = GuessExe(install), LaunchUri = $"steam://rungameid/{id}", CoverPath = SteamCover(steam, id),
                };
            }
        }
    }

    private static string? Vdf(string text, string key) =>
        Regex.Match(text, $"\"{key}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;

    private static string? SteamCover(string steam, string id)
    {
        string cache = Path.Combine(steam, "appcache", "librarycache");
        foreach (var candidate in new[] { Path.Combine(cache, $"{id}_library_600x900.jpg"), Path.Combine(cache, id, "library_600x900.jpg") })
            if (File.Exists(candidate)) return candidate;
        string sub = Path.Combine(cache, id);
        if (Directory.Exists(sub))
            return Directory.EnumerateFiles(sub, "library_600x900*.jpg", SearchOption.AllDirectories).FirstOrDefault();
        return null;
    }

    // ------------------------------------------------------------------ Epic
    private static IEnumerable<GameEntry> Epic()
    {
        string manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!Directory.Exists(manifests)) yield break;
        foreach (var item in Directory.EnumerateFiles(manifests, "*.item"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(item));
            var r = doc.RootElement;
            string Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            if (r.TryGetProperty("bIsApplication", out var isApp) && isApp.ValueKind == JsonValueKind.False) continue;
            string install = Str("InstallLocation"), name = Str("DisplayName"), app = Str("AppName");
            if (install.Length == 0 || name.Length == 0) continue;
            string exe = Str("LaunchExecutable");
            yield return new GameEntry
            {
                Id = "epic:" + app, Name = name, Source = GameSource.Epic, InstallDir = install,
                Exe = exe.Length > 0 ? Path.Combine(install, exe) : GuessExe(install),
                LaunchUri = $"com.epicgames.launcher://apps/{Str("CatalogNamespace")}%3A{Str("CatalogItemId")}%3A{app}?action=launch&silent=true",
            };
        }
    }

    // ------------------------------------------------------------------ GOG
    private static IEnumerable<GameEntry> Gog()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (root is null) yield break;
        foreach (var id in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(id);
            if (k?.GetValue("path") is not string path || k.GetValue("gameName") is not string name) continue;
            var exe = k.GetValue("exe") as string;
            yield return new GameEntry
            {
                Id = "gog:" + id, Name = name, Source = GameSource.GOG, InstallDir = path,
                Exe = exe is { Length: > 0 } && File.Exists(exe) ? exe : GuessExe(path),
                LaunchArgs = k.GetValue("launchParam") as string,
            };
        }
    }

    // ------------------------------------------------------------------ Ubisoft
    private static IEnumerable<GameEntry> Ubisoft()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs");
        if (root is null) yield break;
        foreach (var id in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(id);
            if (k?.GetValue("InstallDir") is not string dir) continue;
            dir = dir.Replace('/', '\\').TrimEnd('\\');
            yield return new GameEntry
            {
                Id = "ubi:" + id, Name = Path.GetFileName(dir), Source = GameSource.Ubisoft, InstallDir = dir,
                Exe = GuessExe(dir), LaunchUri = $"uplay://launch/{id}/0",
            };
        }
    }

    // ------------------------------------------------------------------ Xbox / Microsoft Store (XboxGames folders on every drive)
    private static IEnumerable<GameEntry> Xbox()
    {
        foreach (var drive in FixedDrives())
        {
            string root = Path.Combine(drive, "XboxGames");
            if (!Directory.Exists(root)) continue;
            foreach (var game in Directory.EnumerateDirectories(root))
            {
                string content = Path.Combine(game, "Content");
                if (!Directory.Exists(content)) continue;
                string name = Path.GetFileName(game);
                string? exe = null;
                string config = Path.Combine(content, "MicrosoftGame.config");
                if (File.Exists(config))
                {
                    try
                    {
                        var x = XDocument.Load(config);
                        name = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "ShellVisuals")?.Attribute("DefaultDisplayName")?.Value is { Length: > 0 } dn && !dn.StartsWith("ms-resource") ? dn : name;
                        var exeName = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "Executable")?.Attribute("Name")?.Value;
                        if (exeName is not null && File.Exists(Path.Combine(content, exeName))) exe = Path.Combine(content, exeName);
                    }
                    catch { }
                }
                yield return new GameEntry
                {
                    Id = "xbox:" + Path.GetFileName(game), Name = name, Source = GameSource.Xbox, InstallDir = content,
                    Exe = exe ?? GuessExe(content),
                };
            }
        }
    }

    // ------------------------------------------------------------------ EA · Battle.net · Riot · Rockstar · Amazon (Uninstall registry)
    private static readonly (string Publisher, GameSource Source)[] Publishers =
    [
        ("Electronic Arts", GameSource.EA), ("EA Swiss", GameSource.EA), ("Blizzard", GameSource.BattleNet),
        ("Riot Games", GameSource.Riot), ("Rockstar Games", GameSource.Rockstar), ("Amazon", GameSource.Amazon),
    ];

    private static readonly string[] LauncherNames = ["EA app", "Origin", "Battle.net", "Riot Client", "Riot Vanguard", "Rockstar Games Launcher", "Amazon Games", "Social Club"];

    private static IEnumerable<GameEntry> UninstallRegistry()
    {
        var hives = new (RegistryKey Hive, string Path)[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
        };
        foreach (var (hive, path) in hives)
        {
            using var root = hive.OpenSubKey(path);
            if (root is null) continue;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k is null) continue;
                string publisher = k.GetValue("Publisher") as string ?? "";
                string name = k.GetValue("DisplayName") as string ?? "";
                string uninstall = k.GetValue("UninstallString") as string ?? "";
                var match = Publishers.FirstOrDefault(p => publisher.Contains(p.Publisher, StringComparison.OrdinalIgnoreCase));
                if (match.Publisher is null && uninstall.Contains("Amazon Game Remover", StringComparison.OrdinalIgnoreCase))
                    match = ("Amazon", GameSource.Amazon);
                if (match.Publisher is null || name.Length == 0) continue;
                if (LauncherNames.Any(l => name.StartsWith(l, StringComparison.OrdinalIgnoreCase))) continue;
                string? dir = (k.GetValue("InstallLocation") as string)?.Trim('"', ' ');
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;

                string? uri = null, args = null, exe = GuessExe(dir);
                if (match.Source == GameSource.Riot)
                {
                    // "…\RiotClientServices.exe" --uninstall-product=valorant --uninstall-patchline=live  →  launch the same product
                    var m = Regex.Match(uninstall, "\"([^\"]*RiotClientServices\\.exe)\"\\s+--uninstall-product=(\\S+)\\s+--uninstall-patchline=(\\S+)");
                    if (m.Success) { exe = m.Groups[1].Value; args = $"--launch-product={m.Groups[2].Value} --launch-patchline={m.Groups[3].Value}"; }
                }
                yield return new GameEntry
                {
                    Id = $"{match.Source.ToString().ToLowerInvariant()}:{sub}", Name = name, Source = match.Source,
                    InstallDir = dir.TrimEnd('\\'), Exe = exe, LaunchUri = uri, LaunchArgs = args,
                };
            }
        }
    }

    // ------------------------------------------------------------------ Local: repacks, torrents, AnkerGames, portable installs
    private static readonly string[] LikelyRoots =
    [
        "Games", "Game", "Juegos", "Jogos", "AnkerGames", "Anker Games", "Torrents", "Torrent", "Repacks", "FitGirl Repacks",
        "DODI-Repacks", "GOG Games", "Program Files", "Program Files (x86)", "Epic Games", "SteamLibrary", "Riot Games", "EA Games",
    ];

    private static readonly string[] SkipDirs =
    [
        "Windows", "$Recycle.Bin", "System Volume Information", "ProgramData", "Recovery", "PerfLogs", "Intel", "AMD", "NVIDIA",
        "Common Files", "Microsoft", "Windows Defender", "WindowsApps", "Internet Explorer", "Microsoft Office", "Adobe",
        "dotnet", "Reference Assemblies", "MSBuild", "Microsoft Visual Studio", "Git", "nodejs", "Python", "Docker",
        "Steam", "Epic Games\\Launcher", "Mozilla Firefox", "Google", "BraveSoftware", "Microsoft SDKs", "Windows Kits",
    ];

    private static readonly string[] MoreSkipDirs =
    [
        "Razer", "NVIDIA Corporation", "GOG Galaxy", "Ubisoft Game Launcher", "Riot Vanguard", "Riot Client", "Overwolf",
        "Crack", "Cracks", "Redist", "_Redist", "_CommonRedist", "CommonRedist", "Redistributables", "DirectX", "Support",
        "Extras", "Tools", "Docs", "Manual", "Soundtrack", "OST", "Artbook", "Mods", "Saves", "Screenshots",
    ];

    private static readonly string[] SkipPrefixes = ["UE_", "Unreal Engine", "Microsoft ", "Windows "];

    /// <summary>Folder names that describe a layout, not a game: take the parent's name instead.</summary>
    private static readonly HashSet<string> GenericLeaves = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "binaries", "win64", "x64", "x86", "game", "retail", "content", "live", "app", "client" };

    private static IEnumerable<GameEntry> LocalScan(HashSet<string> known, IProgress<string>? progress, CancellationToken ct)
    {
        var results = new List<GameEntry>();
        var roots = new List<string>();
        foreach (var drive in FixedDrives())
        {
            foreach (var name in LikelyRoots)
            {
                string p = Path.Combine(drive, name);
                if (Directory.Exists(p)) roots.Add(p);
            }
            // top-level folders of non-system drives often ARE games (D:\Cyberpunk 2077)
            if (!drive.StartsWith(Path.GetPathRoot(Environment.SystemDirectory)!, StringComparison.OrdinalIgnoreCase))
                roots.Add(drive);
        }
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) roots.Add(downloads);

        int visited = 0;
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            progress?.Report("Escaneando " + root);
            // Program Files is full of ordinary apps with Inno Setup uninstallers: require an engine fingerprint there.
            bool strict = root.Contains("Program Files", StringComparison.OrdinalIgnoreCase);
            Walk(root, 0, strict);
        }
        return results;

        void Walk(string dir, int depth, bool strict)
        {
            if (ct.IsCancellationRequested || visited > 25000 || depth > 3) return;
            visited++;
            string leaf = Path.GetFileName(dir.TrimEnd('\\'));
            if (depth > 0 && SkipDirs.Concat(MoreSkipDirs).Any(s => leaf.Equals(s, StringComparison.OrdinalIgnoreCase) || dir.EndsWith("\\" + s, StringComparison.OrdinalIgnoreCase))) return;
            if (depth > 0 && SkipPrefixes.Any(p => leaf.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return;
            if (known.Contains(Normalize(dir))) return;

            if (depth > 0 && LooksLikeGame(dir, strict, out string? exe))
            {
                string nameLeaf = leaf;
                for (string? d = dir; d is not null && GenericLeaves.Contains(nameLeaf);)
                {
                    d = Path.GetDirectoryName(d);
                    nameLeaf = d is null ? leaf : Path.GetFileName(d);
                }
                bool underEpic = dir.Contains(@"\Epic Games\", StringComparison.OrdinalIgnoreCase);
                results.Add(new GameEntry
                {
                    Id = "local:" + Normalize(dir).ToLowerInvariant(), Name = PrettyName(nameLeaf), Source = underEpic ? GameSource.Epic : GameSource.Local,
                    InstallDir = dir, Exe = exe,
                    // Epic folders are named after the AppName, which the launcher accepts on its own.
                    LaunchUri = underEpic ? $"com.epicgames.launcher://apps/{leaf}?action=launch&silent=true" : null,
                });
                return; // don't descend into a game
            }
            string[] children;
            try { children = Directory.GetDirectories(dir); } catch { return; }
            foreach (var child in children)
            {
                try
                {
                    if ((File.GetAttributes(child) & (FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
                }
                catch { continue; }
                Walk(child, depth + 1, strict);
            }
        }
    }

    /// <summary>Engine fingerprints: Unity (X.exe + X_Data), Unreal (Engine\ + *-Shipping.exe), Steamworks DLLs, repack uninstallers.</summary>
    internal static bool LooksLikeGame(string dir, bool strict, out string? exe)
    {
        exe = null;
        string[] files, dirs;
        try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); } catch { return false; }
        var exes = files.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
        var dirNames = dirs.Select(d => Path.GetFileName(d)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fileNames = files.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Unity
        foreach (var e in exes)
            if (dirNames.Contains(Path.GetFileNameWithoutExtension(e) + "_Data")) { exe = e; return true; }
        // Unreal
        if (dirNames.Contains("Engine"))
        {
            foreach (var d in dirs)
            {
                string bin = Path.Combine(d, "Binaries", "Win64");
                if (!Directory.Exists(bin)) continue;
                var shipping = Directory.EnumerateFiles(bin, "*-Shipping.exe").Where(IsPlayableExe)
                    .OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
                if (shipping is not null) { exe = exes.Where(IsPlayableExe).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault() ?? shipping; return true; }
            }
        }
        // Steamworks / repack markers next to a real executable
        bool markers = fileNames.Contains("steam_api64.dll") || fileNames.Contains("steam_api.dll") || (!strict && fileNames.Contains("unins000.exe"))
                    || fileNames.Contains("goggame.dll") || fileNames.Any(n => n!.StartsWith("goggame-", StringComparison.OrdinalIgnoreCase))
                    || (!strict && (dirNames.Contains("_CommonRedist") || dirNames.Contains("__Installer"))) || fileNames.Contains("EOSSDK-Win64-Shipping.dll");
        if (markers)
        {
            exe = GuessExe(dir);
            return exe is not null;
        }
        return false;
    }

    private static readonly string[] NotTheGame =
    [
        "unins", "setup", "install", "crash", "redist", "vcredist", "vc_redist", "dxsetup", "dotnet", "UnityCrashHandler",
        "EasyAntiCheat", "BEService", "BattlEye", "report", "helper", "updater", "update", "launcherpatcher", "cefprocess",
        "QuickSFV", "7z", "config", "settings", "benchmark", "touchup", "cleanup", "prereq", "uplay", "ubisoftconnect",
    ];

    private static bool IsPlayableExe(string path)
    {
        string n = Path.GetFileNameWithoutExtension(path);
        return !NotTheGame.Any(bad => n.Contains(bad, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Largest playable exe in the folder, then in common binary sub-folders.</summary>
    public static string? GuessExe(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        foreach (var sub in new[] { "", "bin", "Bin", @"bin\x64", @"Binaries\Win64", "x64", "Game", @"Game\bin\x64" })
        {
            string d = Path.Combine(dir, sub);
            if (!Directory.Exists(d)) continue;
            try
            {
                var best = Directory.EnumerateFiles(d, "*.exe").Where(IsPlayableExe)
                    .Select(f => new FileInfo(f)).Where(f => f.Length > 256 * 1024)
                    .OrderByDescending(f => f.Length).FirstOrDefault();
                if (best is not null) return best.FullName;
            }
            catch { }
        }
        return null;
    }

    public static IEnumerable<string> FixedDrives() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName);

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\');

    private static string PrettyName(string folder)
    {
        string s = Regex.Replace(folder, @"[\._]+", " ");
        s = Regex.Replace(s, @"\s*[\[\(].*?[\]\)]\s*", " ");                 // [FitGirl Repack], (v1.2)
        s = Regex.Replace(s, @"\b(repack|fitgirl|dodi|elamigos|gog|codex|empress|flt|tenoke|rune)\b", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bv?\d+(\.\d+){1,3}\b", "");
        return Regex.Replace(s, @"\s{2,}", " ").Trim(' ', '-');
    }
}
