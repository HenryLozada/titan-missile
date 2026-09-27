namespace Titan.Engine.Processes;

public enum ProcessAction
{
    /// <summary>Never touched.</summary>
    Protected,
    /// <summary>Left as is.</summary>
    None,
    /// <summary>BelowNormal priority + EcoQoS. Keeps working (voice, music), just yields to the game.</summary>
    Throttle,
    /// <summary>Suspended with NtSuspendProcess and resumed after the session.</summary>
    Freeze,
}

/// <summary>
/// Which background process gets what. Names are exe file names, case-insensitive.
/// Defaults are conservative: freezing is only for things that are safe to stop mid-flight;
/// voice/music/browsers are throttled instead, so a Discord call never drops.
/// </summary>
public sealed class ProcessPolicy
{
    public static readonly IReadOnlySet<string> AlwaysProtected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Windows core
        "System", "Idle", "Registry", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe",
        "lsass.exe", "lsaiso.exe", "svchost.exe", "dwm.exe", "explorer.exe", "fontdrvhost.exe", "audiodg.exe",
        "ctfmon.exe", "sihost.exe", "taskhostw.exe", "RuntimeBroker.exe", "ShellExperienceHost.exe",
        "StartMenuExperienceHost.exe", "SearchHost.exe", "TextInputHost.exe", "conhost.exe", "WmiPrvSE.exe",
        "MsMpEng.exe", "NisSrv.exe", "SecurityHealthService.exe", "spoolsv.exe", "dllhost.exe", "MemCompression",
        // Anti-cheat — touching these gets people banned or kicked
        "EasyAntiCheat.exe", "EasyAntiCheat_EOS.exe", "BEService.exe", "BEService_x64.exe", "vgc.exe", "vgtray.exe",
        "FACEIT.exe", "faceitservice.exe", "EAAntiCheat.GameServiceLauncher.exe", "EAAntiCheat.GameService.exe",
        "GameGuard.exe", "xigncode.exe", "PnkBstrA.exe", "PnkBstrB.exe", "atvi-randgrid_sr.exe",
        // Overlays, capture, GPU & frame tooling
        "GameBar.exe", "GameBarFTServer.exe", "GameInputSvc.exe", "nvcontainer.exe", "NVIDIA Overlay.exe",
        "nvsphelper64.exe", "NVDisplay.Container.exe", "amdow.exe", "RadeonSoftware.exe", "AMDRSServ.exe",
        "atiesrxx.exe", "atieclxx.exe", "RTSS.exe", "RTSSHooksLoader64.exe", "MSIAfterburner.exe",
        "obs64.exe", "obs-ffmpeg-mux.exe", "PresentMon.exe", "PresentMonService.exe",
        // Peripherals: freezing these kills macros, mice DPI, headset EQ
        "RazerAppEngine.exe", "Razer Synapse Service.exe", "Razer Synapse 3.exe", "lghub.exe", "lghub_agent.exe",
        "lghub_updater.exe", "iCUE.exe", "Corsair.Service.exe", "ArmouryCrate.exe", "ArmouryCrate.Service.exe",
        "SteelSeriesGG.exe", "SteelSeriesEngine.exe", "WaveLink.exe",
        // Launchers the running game may depend on (DRM, overlay, cloud saves)
        "steam.exe", "steamservice.exe", "steamwebhelper.exe", "GameOverlayUI.exe", "EpicWebHelper.exe",
        "EADesktop.exe", "EABackgroundService.exe", "Battle.net.exe", "Agent.exe", "RiotClientServices.exe",
        "upc.exe", "UbisoftConnect.exe", "GalaxyClient.exe", "XboxPcApp.exe", "gamingservices.exe",
        // Us
        "titan.exe", "Titan.App.exe", "Titan.Engine.exe", "Titan.Tray.exe",
    };

    public static readonly IReadOnlyList<string> DefaultFreeze =
    [
        "OneDrive.exe", "Dropbox.exe", "GoogleDriveFS.exe", "MEGAsync.exe", "Widgets.exe", "WidgetService.exe",
        "PhoneExperienceHost.exe", "YourPhone.exe", "Copilot.exe",
        "AdobeCollabSync.exe", "Creative Cloud.exe", "CCXProcess.exe", "AdobeIPCBroker.exe",
        "SearchApp.exe", "Cortana.exe", "OneDriveStandaloneUpdater.exe", "CrossDeviceService.exe",
        "HxTsr.exe", "HxOutlook.exe", "SkypeApp.exe", "SkypeBackgroundHost.exe", "msteamsupdate.exe",
    ];

    public static readonly IReadOnlyList<string> DefaultThrottle =
    [
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "opera_gx.exe", "vivaldi.exe",
        "Discord.exe", "Spotify.exe", "Slack.exe", "ms-teams.exe", "Teams.exe", "Telegram.exe", "WhatsApp.exe",
        "EpicGamesLauncher.exe", "Code.exe", "Notion.exe", "Zoom.exe", "ts3client_win64.exe", "TeamSpeak.exe",
        "Overwolf.exe", "wallpaper32.exe", "wallpaper64.exe",
    ];

    /// <summary>
    /// Voice and music: throttled like the rest, but their memory is never trimmed or demoted —
    /// a re-fault in the middle of a call is an audible glitch.
    /// </summary>
    public static readonly IReadOnlySet<string> Responsive = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Discord.exe", "Spotify.exe", "ms-teams.exe", "Teams.exe", "Slack.exe", "Telegram.exe", "WhatsApp.exe",
        "Zoom.exe", "ts3client_win64.exe", "TeamSpeak.exe",
    };

    private readonly HashSet<string> _freeze;
    private readonly HashSet<string> _throttle;

    public ProcessPolicy(IEnumerable<string>? freeze = null, IEnumerable<string>? throttle = null)
    {
        _freeze = new(freeze ?? DefaultFreeze, StringComparer.OrdinalIgnoreCase);
        _throttle = new(throttle ?? DefaultThrottle, StringComparer.OrdinalIgnoreCase);
    }

    public ProcessAction Classify(ProcessEntry p, int selfPid, IReadOnlySet<int> gameTree)
    {
        if (p.Pid <= 4 || p.Pid == selfPid || p.SessionId == 0) return ProcessAction.Protected;
        if (gameTree.Contains(p.Pid)) return ProcessAction.Protected;
        if (AlwaysProtected.Contains(p.Name)) return ProcessAction.Protected;
        if (_freeze.Contains(p.Name)) return ProcessAction.Freeze;
        if (_throttle.Contains(p.Name)) return ProcessAction.Throttle;
        return ProcessAction.None;
    }

    /// <summary>Crash reporters, uninstallers and redistributables living in a game folder are not the game.</summary>
    public static bool IsGameHelper(string exe) =>
        exe.Contains("crash", StringComparison.OrdinalIgnoreCase) || exe.Contains("report", StringComparison.OrdinalIgnoreCase)
        || exe.StartsWith("unins", StringComparison.OrdinalIgnoreCase) || exe.Contains("redist", StringComparison.OrdinalIgnoreCase)
        || exe.Contains("setup", StringComparison.OrdinalIgnoreCase) || exe.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>True if <paramref name="path"/> is inside <paramref name="dir"/> (not merely sharing a prefix).</summary>
    public static bool IsUnder(string? path, string dir)
    {
        if (path is null) return false;
        string root = dir.Replace('/', '\\').TrimEnd('\\') + "\\";
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The game, everything it spawned, and the chain of parents that launched it.</summary>
    public static HashSet<int> GameTree(IReadOnlyList<ProcessEntry> all, int? gamePid)
    {
        var tree = new HashSet<int>();
        if (gamePid is not int root) return tree;

        var byPid = all.ToDictionary(p => p.Pid);
        // ancestors (launcher → game); stop at session 0 / explorer
        for (int cur = root; byPid.TryGetValue(cur, out var p) && tree.Add(cur);)
        {
            if (p.ParentPid == 0 || p.Name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) break;
            cur = p.ParentPid;
        }
        // descendants
        var queue = new Queue<int>([root]);
        while (queue.Count > 0)
        {
            int parent = queue.Dequeue();
            foreach (var child in all.Where(c => c.ParentPid == parent && c.Pid != parent))
                if (tree.Add(child.Pid)) queue.Enqueue(child.Pid);
        }
        return tree;
    }
}
