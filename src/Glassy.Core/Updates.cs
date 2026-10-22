using System.Net.Http.Headers;
using System.Text.Json;

namespace Glassy.Core;

/// <summary>Asks GitHub for the latest release tag. Installing it is install.ps1's job, the same script a first install
/// runs, so updating has exactly one code path.</summary>
public static class Updates
{
    public const string ReleasesPage = "https://github.com/SuperBartimus/GlassySystemGadget/releases/latest";
    public const string InstallScript = "https://raw.githubusercontent.com/SuperBartimus/GlassySystemGadget/main/install.ps1";
    const string LatestApi = "https://api.github.com/repos/SuperBartimus/GlassySystemGadget/releases/latest";
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GlassySystemGadget", "1"));   // GitHub's API rejects requests without one
        return h;
    }

    /// <summary>"v0.8.1" or "0.8.1" -> 0.8.1; null for anything else.</summary>
    public static Version ParseTag(string tag) =>
        tag != null && Version.TryParse(tag.TrimStart('v', 'V'), out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;

    /// <summary>Compares Major.Minor.Build only - the assembly's fourth part is always 0 and tags don't have one.</summary>
    public static bool IsNewer(Version latest, Version current) =>
        latest != null && (current == null || latest > new Version(current.Major, current.Minor, Math.Max(0, current.Build)));

    /// <summary>Whether a scheduled check is due. Never more than once an hour, whatever the setting says.</summary>
    public static bool Due(DateTime? lastCheckUtc, int hours, DateTime nowUtc) =>
        lastCheckUtc is not { } last || nowUtc - last >= TimeSpan.FromHours(Math.Max(1, hours)) || last > nowUtc;   // a clock set back doesn't stall checks

    public enum Action { None, Notify, Install, Wait }

    /// <summary>What a scheduled check does with the latest release. Skipping a version only silences the prompt; Auto
    /// installs only on a copy install.ps1 put in place (any other copy can only be pointed at the release page, so it
    /// gets the prompt instead), and with OvernightOnly it waits for 1-5 AM local.</summary>
    public static Action Decide(GlobalConfig g, Version latest, Version current, bool installedCopy, DateTime nowLocal)
    {
        if (g.UpdateMode == UpdateMode.Off || !IsNewer(latest, current)) return Action.None;
        if (g.UpdateMode == UpdateMode.Auto && installedCopy)
            return g.UpdateOvernightOnly && (nowLocal.Hour < 1 || nowLocal.Hour >= 5) ? Action.Wait : Action.Install;
        return ParseTag(g.UpdateSkipVersion) == latest ? Action.None : Action.Notify;
    }

    public static async Task<string> LatestTagAsync()
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(LatestApi));
        return doc.RootElement.GetProperty("tag_name").GetString();
    }
}
