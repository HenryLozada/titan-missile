using System.Net.Http;
using System.Text.Json;
using Titan.Engine.Games;

namespace Titan.App.Services;

/// <summary>
/// Downloads official vertical box art from Steam's CDN. The game is matched by name against Steam's store
/// search (many Epic/GOG/local titles also exist on Steam), then its 600x900 "library capsule" is pulled —
/// the original art, high quality, no API key. Only the game name is sent; covers are cached on disk forever.
/// Games with no Steam match keep their icon.
/// </summary>
public sealed class CoverService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly string _dir = Path.Combine(AppData.Dir, "covers");
    private readonly HashSet<string> _missed;          // ids we searched and found nothing for — don't retry every launch
    private readonly string _missPath;

    public CoverService()
    {
        Directory.CreateDirectory(_dir);
        _missPath = Path.Combine(_dir, "_nomatch.txt");
        _missed = File.Exists(_missPath) ? [.. File.ReadAllLines(_missPath)] : [];
        if (!Http.DefaultRequestHeaders.Contains("User-Agent"))
            Http.DefaultRequestHeaders.Add("User-Agent", "TITAN/1.0");
    }

    /// <summary>Local cover file for this game if already downloaded, else null.</summary>
    public string? Local(GameEntry g)
    {
        string p = PathFor(g.Id);
        return File.Exists(p) ? p : null;
    }

    private string PathFor(string id)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) id = id.Replace(c, '_');
        return Path.Combine(_dir, id.Replace(':', '_') + ".jpg");
    }

    /// <summary>
    /// Fills covers for every game that has neither a launcher-supplied cover nor a downloaded one.
    /// <paramref name="onCover"/> fires (id, path) each time one lands, so the grid can refresh incrementally.
    /// </summary>
    public async Task FetchMissingAsync(IReadOnlyList<GameEntry> games, Action<string, string> onCover, CancellationToken ct = default)
    {
        foreach (var g in games)
        {
            if (ct.IsCancellationRequested) break;
            if (g.CoverPath is not null || Local(g) is not null || _missed.Contains(g.Id)) continue;
            try
            {
                string? path = await FetchOneAsync(g, ct);
                if (path is not null) onCover(g.Id, path);
                else MarkMissed(g.Id);
            }
            catch (OperationCanceledException) { break; }
            catch { /* one failure must not stop the batch; retry it next session */ }
        }
    }

    private async Task<string?> FetchOneAsync(GameEntry g, CancellationToken ct)
    {
        int? appId = g.Source == GameSource.Steam && g.Id.StartsWith("steam:") && int.TryParse(g.Id[6..], out int direct)
            ? direct
            : await SearchSteamAsync(g.Name, ct);
        if (appId is not int id) return null;

        foreach (var url in new[]
        {
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{id}/library_600x900_2x.jpg",
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{id}/library_600x900.jpg",
        })
        {
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) continue;
            if (resp.Content.Headers.ContentType?.MediaType is not { } mt || !mt.StartsWith("image/")) continue;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length < 2000) continue;                    // Steam returns a tiny placeholder for missing art
            string path = PathFor(g.Id);
            await File.WriteAllBytesAsync(path, bytes, ct);
            return path;
        }
        return null;
    }

    /// <summary>Steam store search by name; returns the appid only if the top hit's name really matches.</summary>
    private static async Task<int?> SearchSteamAsync(string name, CancellationToken ct)
    {
        string url = "https://store.steampowered.com/api/storesearch/?l=en&cc=us&term=" + Uri.EscapeDataString(name);
        using var resp = await Http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync(ct));
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return null;

        string want = Normalize(name);
        if (want.Length < 4) return null;
        int? edition = null;
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idEl) || !item.TryGetProperty("name", out var nameEl)) continue;
            string got = Normalize(nameEl.GetString() ?? "");
            if (got.Length == 0) continue;
            // Exact name wins outright. A store title that *extends* ours (e.g. "…V Enhanced") is the same game's
            // edition — keep the first as a fallback. We never accept a title that merely *contains* ours
            // ("CONVERGENCE: A League of Legends Story" is not League of Legends).
            if (got == want) return idEl.GetInt32();
            if (edition is null && got.StartsWith(want, StringComparison.Ordinal)) edition = idEl.GetInt32();
        }
        return edition;
    }

    private static string Normalize(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        int n = 0;
        foreach (char c in s.ToLowerInvariant())
            if (char.IsLetterOrDigit(c)) buf[n++] = c;
        return new string(buf[..n]);
    }

    private void MarkMissed(string id)
    {
        if (_missed.Add(id))
            try { File.AppendAllText(_missPath, id + Environment.NewLine); } catch { }
    }
}
