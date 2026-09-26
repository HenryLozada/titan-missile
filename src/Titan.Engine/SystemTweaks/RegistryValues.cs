using Microsoft.Win32;

namespace Titan.Engine.SystemTweaks;

/// <summary>A DWORD value captured before we changed it. <c>null</c> means it did not exist and must be deleted on restore.</summary>
public sealed record DwordValue(string Hive, string Key, string Name, uint? Value);

public static class RegistryValues
{
    public const string MmcssKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";

    public static DwordValue ReadDword(string hive, string key, string name)
    {
        using var k = Root(hive).OpenSubKey(key);
        object? v = k?.GetValue(name);
        return new DwordValue(hive, key, name, v is int i ? unchecked((uint)i) : null);
    }

    public static void Write(DwordValue value)
    {
        if (value.Value is uint v)
        {
            using var k = Root(value.Hive).CreateSubKey(value.Key, writable: true);
            k.SetValue(value.Name, unchecked((int)v), RegistryValueKind.DWord);
        }
        else
        {
            using var k = Root(value.Hive).OpenSubKey(value.Key, writable: true);
            k?.DeleteValue(value.Name, throwOnMissingValue: false);
        }
    }

    private static RegistryKey Root(string hive) => hive switch
    {
        "HKLM" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        "HKCU" => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
        _ => throw new ArgumentException($"Hive no soportado: {hive}"),
    };
}
