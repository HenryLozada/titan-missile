using Titan.Engine.Games;

namespace Titan.Engine.Tests;

public sealed class GameLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "titan-games-" + Guid.NewGuid().ToString("N"));

    private string Make(string rel, int bytes = 0)
    {
        string p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[bytes]);
        return p;
    }

    [Fact]
    public void Unity_game_is_detected_by_exe_plus_data_folder()
    {
        Make(@"Hollow\Hollow.exe", 600_000);
        Make(@"Hollow\Hollow_Data\globalgamemanagers", 10);
        Assert.True(GameLibrary.LooksLikeGame(Path.Combine(_root, "Hollow"), strict: true, out var exe));
        Assert.EndsWith("Hollow.exe", exe);
    }

    [Fact]
    public void Unreal_game_picks_the_game_not_the_uninstall_helper()
    {
        Make(@"Arena\Engine\Binaries\ThirdParty\x.dll", 10);
        Make(@"Arena\ArenaGame\Binaries\Win64\UninstallHelper-Win64-Shipping.exe", 900_000);
        Make(@"Arena\ArenaGame\Binaries\Win64\ArenaClient-Win64-Shipping.exe", 800_000);
        Assert.True(GameLibrary.LooksLikeGame(Path.Combine(_root, "Arena"), strict: true, out var exe));
        Assert.EndsWith("ArenaClient-Win64-Shipping.exe", exe);
    }

    [Fact]
    public void Repack_uninstaller_counts_only_outside_program_files()
    {
        Make(@"Repack\Game.exe", 700_000);
        Make(@"Repack\unins000.exe", 900_000);
        Assert.True(GameLibrary.LooksLikeGame(Path.Combine(_root, "Repack"), strict: false, out var exe));
        Assert.EndsWith("Game.exe", exe);
        Assert.False(GameLibrary.LooksLikeGame(Path.Combine(_root, "Repack"), strict: true, out _));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
