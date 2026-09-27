using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Titan.Engine.Processes;
using Titan.Engine.SystemTweaks;

namespace Titan.Engine.Session;

// ---------------------------------------------------------------- entries: one per change we make, with what's needed to undo it

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PowerPlanChanged), "powerPlan")]
[JsonDerivedType(typeof(PowerOverlayChanged), "powerOverlay")]
[JsonDerivedType(typeof(ServiceStopped), "serviceStopped")]
[JsonDerivedType(typeof(ProcessFrozen), "processFrozen")]
[JsonDerivedType(typeof(PriorityChanged), "priority")]
[JsonDerivedType(typeof(EcoQoSApplied), "ecoQoS")]
[JsonDerivedType(typeof(CpuSetsChanged), "cpuSets")]
[JsonDerivedType(typeof(RegistryChanged), "registry")]
public abstract record JournalEntry
{
    public abstract string Describe();
}

public sealed record PowerPlanChanged(Guid Previous) : JournalEntry
{ public override string Describe() => $"Plan de energía → {Previous}"; }

public sealed record PowerOverlayChanged(Guid Previous) : JournalEntry
{ public override string Describe() => $"Modo de energía → {Previous}"; }

public sealed record ServiceStopped(string Name) : JournalEntry
{ public override string Describe() => $"Servicio {Name}"; }

public sealed record ProcessFrozen(int Pid, long CreationTime, string Name) : JournalEntry
{ public override string Describe() => $"Reanudar {Name} ({Pid})"; }

public sealed record PriorityChanged(int Pid, long CreationTime, string Name, PriorityClass Previous) : JournalEntry
{ public override string Describe() => $"Prioridad {Name} → {Previous}"; }

public sealed record EcoQoSApplied(int Pid, long CreationTime, string Name) : JournalEntry
{ public override string Describe() => $"EcoQoS {Name}"; }

public sealed record CpuSetsChanged(int Pid, long CreationTime, string Name, uint[] Previous) : JournalEntry
{ public override string Describe() => $"CPU sets {Name}"; }

public sealed record RegistryChanged(DwordValue Previous) : JournalEntry
{ public override string Describe() => $"Registro {Previous.Name}"; }

public sealed record JournalDocument(Guid SessionId, DateTimeOffset StartedAt, int? GamePid, string? GameName, List<JournalEntry> Entries);

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(JournalDocument))]
internal partial class JournalJsonContext : JsonSerializerContext;

/// <summary>
/// Write-ahead log of a boost session. Every change is persisted BEFORE it is applied, so if TITAN
/// (or Windows) dies mid-boost the next start can undo everything. Every undo is idempotent, so
/// undoing an intent that never got applied is harmless.
/// </summary>
public sealed class Journal
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TITAN", "journal.json");

    private readonly string _path;
    private readonly object _gate = new();
    private JournalDocument? _doc;

    private readonly bool _secure;

    /// <param name="path">Custom location (tests). The default location gets an admin-only ACL.</param>
    public Journal(string? path = null)
    {
        _path = path ?? DefaultPath;
        _secure = path is null;
    }

    public string FilePath => _path;
    public bool HasOpenSession => File.Exists(_path);
    public JournalDocument? Current => _doc;

    public JournalDocument Begin(int? gamePid, string? gameName)
    {
        lock (_gate)
        {
            if (HasOpenSession) throw new InvalidOperationException("Ya hay una sesión de boost abierta. Restaura primero.");
            _doc = new JournalDocument(Guid.NewGuid(), DateTimeOffset.Now, gamePid, gameName, []);
            Flush();
            return _doc;
        }
    }

    public void Record(JournalEntry entry)
    {
        lock (_gate)
        {
            (_doc ?? throw new InvalidOperationException("No hay sesión.")).Entries.Add(entry);
            Flush();
        }
    }

    /// <summary>Drops an entry whose apply failed and was never partially applied.</summary>
    public void Forget(JournalEntry entry)
    {
        lock (_gate)
        {
            if (_doc?.Entries.Remove(entry) == true) Flush();
        }
    }

    public JournalDocument? Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return _doc = null;
            using var fs = File.OpenRead(_path);
            return _doc = JsonSerializer.Deserialize(fs, JournalJsonContext.Default.JournalDocument);
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            File.Delete(_path);
            _doc = null;
        }
    }

    private void Flush()
    {
        var dir = Path.GetDirectoryName(_path)!;
        if (_secure) EnsureSecureDirectory(dir);
        else Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(fs, _doc!, JournalJsonContext.Default.JournalDocument);
        }
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>
    /// The journal drives privileged undo actions, so only SYSTEM and Administrators may write it.
    /// (%ProgramData% lets normal users create files by default.)
    /// </summary>
    public static void EnsureSecureDirectory(string dir)
    {
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));

        var info = new DirectoryInfo(dir);
        if (!info.Exists) info.Create(sec);
        else info.SetAccessControl(sec);
    }
}
