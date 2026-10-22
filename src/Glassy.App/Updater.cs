using System.ComponentModel;
using System.Diagnostics;
using Glassy.Core;

namespace Glassy.App;

/// <summary>Checking for and installing updates - from the right-click menu, Settings > Updates, and a schedule.
/// Installing is always install.ps1, the same script a first install runs: it downloads the latest release, stops
/// this process, replaces the files and starts the new version. Scheduled checks honour UpdateCheckHours (at least an
/// hour apart, persisted so restarts don't add calls); a release waiting for the overnight window is held in memory,
/// not re-fetched.</summary>
public sealed class Updater : IDisposable
{
    const string Title = "Glassy System Gadget";
    readonly AppConfig cfg; readonly Action changed;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 10 * 60_000 };
    string pendingTag = "", laterTag = ""; DateTime laterUntil;
    bool busy;

    /// <summary>Shown on the Updates page.</summary>
    public string LastResult { get; private set; } = "";

    public Updater(AppConfig cfg, Action changed)
    {
        this.cfg = cfg; this.changed = changed;
        timer.Tick += (_, _) => _ = Scheduled();
        timer.Start();
        // First scheduled look a couple of minutes after start, not during the startup burst.
        var first = new System.Windows.Forms.Timer { Interval = 2 * 60_000 };
        first.Tick += (_, _) => { first.Dispose(); _ = Scheduled(); };
        first.Start();
    }

    public static Version Current => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
    public static string CurrentText => Current is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "dev";

    /// <summary>True when this copy is the one install.ps1 put in %LocalAppData% - the only one it can replace.</summary>
    public static bool InstalledCopy =>
        string.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassySystemGadget"), StringComparison.OrdinalIgnoreCase);

    async Task Scheduled()
    {
        var g = cfg.Global;
        if (busy || g.UpdateMode == UpdateMode.Off) return;
        if (pendingTag != "") { Act(pendingTag, scheduled: true); return; }   // waiting for the overnight window: no new call
        if (!Updates.Due(g.UpdateLastCheckUtc, g.UpdateCheckHours, DateTime.UtcNow)) return;
        var tag = await Fetch(null);
        if (tag != null) Act(tag, scheduled: true);
    }

    /// <summary>"Check now": always asks GitHub and always says what it found.</summary>
    public async Task CheckNow(IWin32Window owner)
    {
        if (busy) return;
        var tag = await Fetch(owner);
        if (tag == null) return;
        if (!Updates.IsNewer(Updates.ParseTag(tag), Current))
        {
            MessageBox.Show(owner, $"You're up to date (v{CurrentText}).", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Prompt(owner, tag);
    }

    async Task<string> Fetch(IWin32Window owner)
    {
        busy = true;
        try
        {
            string tag = await Updates.LatestTagAsync();
            cfg.Global.UpdateLastCheckUtc = DateTime.UtcNow;
            LastResult = $"Checked {DateTime.Now:MMM d, HH:mm}: latest is {tag}";
            changed();
            return tag;
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is System.Text.Json.JsonException || ex is KeyNotFoundException || ex is InvalidOperationException)
        {
            LastResult = $"Check failed {DateTime.Now:MMM d, HH:mm}: {ex.Message}";
            AppLog.Write("update check failed: " + ex.Message);
            if (owner != null) MessageBox.Show(owner, "Couldn't reach GitHub to check for updates.\n\n" + ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        finally { busy = false; }
    }

    void Act(string tag, bool scheduled)
    {
        switch (Updates.Decide(cfg.Global, Updates.ParseTag(tag), Current, InstalledCopy, DateTime.Now))
        {
            case Updates.Action.Install: pendingTag = ""; AppLog.Write($"auto-update: installing {tag}"); RunInstaller(silent: true); break;
            case Updates.Action.Wait: pendingTag = tag; break;
            case Updates.Action.Notify:
                pendingTag = "";
                if (scheduled && laterTag == tag && DateTime.Now < laterUntil) return;   // "Later" holds for a day
                Prompt(null, tag);
                break;
            default: pendingTag = ""; break;
        }
    }

    /// <summary>Install now / Later / Skip this version, with a link to the release notes. A copy that isn't the
    /// installed one can't replace itself, so it offers the release page instead.</summary>
    void Prompt(IWin32Window owner, string tag)
    {
        if (!InstalledCopy)
        {
            if (MessageBox.Show(owner, $"{tag} is available (this is v{CurrentText}).\n\nThis copy isn't the installed one, so it can't update itself. Open the release page?",
                    Title, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes) OpenUrl(Updates.ReleasesPage);
            return;
        }
        var install = new TaskDialogButton("Install now"); var later = new TaskDialogButton("Later"); var skip = new TaskDialogButton("Skip this version");
        var page = new TaskDialogPage
        {
            Caption = Title, Heading = $"{tag} is available",
            Text = $"This is v{CurrentText}. Installing closes the gadget, updates it and starts it again; a PowerShell window shows the progress.\n\n<a href=\"notes\">Release notes</a>",
            EnableLinks = true, Icon = TaskDialogIcon.Information, Buttons = { install, later, skip }, DefaultButton = install,
        };
        page.LinkClicked += (_, _) => OpenUrl(Updates.ReleasesPage);
        var result = owner != null ? TaskDialog.ShowDialog(owner, page) : TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen);
        if (result == install) RunInstaller(silent: false);
        else if (result == skip) { cfg.Global.UpdateSkipVersion = tag; changed(); }
        else { laterTag = tag; laterUntil = DateTime.Now.AddDays(1); }
    }

    /// <summary>Runs install.ps1 from GitHub. Visible: a PowerShell window that stays open if it fails. Silent: no
    /// window, everything it prints goes to update.log next to the config.</summary>
    public void RunInstaller(bool silent)
    {
        string script;
        if (silent)
        {
            string log = Path.Combine(Path.GetDirectoryName(ConfigStore.DefaultPath)!, "update.log").Replace("'", "''");
            script = $"& {{ irm {Updates.InstallScript} | iex }} *> '{log}'";
        }
        else script = $"try {{ irm {Updates.InstallScript} | iex }} catch {{ Write-Host $_ -ForegroundColor Red; Read-Host 'Update failed - press Enter to close' }}";
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass {(silent ? "-WindowStyle Hidden " : "")}-Command \"{script}\"")
        { UseShellExecute = !silent, CreateNoWindow = silent };
        try { Process.Start(psi); }
        catch (Win32Exception ex) { AppLog.Write("update launch failed: " + ex.Message); if (!silent) MessageBox.Show("Couldn't start PowerShell: " + ex.Message, Title); }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Win32Exception) { }
    }

    public void Dispose() => timer.Dispose();
}
