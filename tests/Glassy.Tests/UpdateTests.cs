using Glassy.Core;
using Xunit;

namespace Glassy.Tests;

public class UpdateTests
{
    static readonly Version Current = new(0, 10, 1, 0);
    static readonly Version Newer = new(0, 11, 0);
    static readonly DateTime Noon = new(2026, 10, 22, 12, 0, 0), ThreeAm = new(2026, 10, 23, 3, 0, 0);

    static GlobalConfig G(UpdateMode mode, bool overnight = false, string skip = "") =>
        new() { UpdateMode = mode, UpdateOvernightOnly = overnight, UpdateSkipVersion = skip };

    [Fact]
    public void Scheduled_checks_are_at_least_an_hour_apart_and_survive_a_clock_set_back()
    {
        var now = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(Updates.Due(null, 6, now));                                   // never checked
        Assert.False(Updates.Due(now.AddHours(-5), 6, now));
        Assert.True(Updates.Due(now.AddHours(-6), 6, now));
        Assert.False(Updates.Due(now.AddMinutes(-30), 0, now));                   // 0 hours is still at most hourly
        Assert.True(Updates.Due(now.AddDays(3), 6, now));                         // last check "in the future": clock moved back
    }

    [Fact]
    public void Notify_asks_unless_that_version_was_skipped()
    {
        Assert.Equal(Updates.Action.Notify, Updates.Decide(G(UpdateMode.Notify), Newer, Current, true, Noon));
        Assert.Equal(Updates.Action.None, Updates.Decide(G(UpdateMode.Notify, skip: "v0.11.0"), Newer, Current, true, Noon));
        Assert.Equal(Updates.Action.Notify, Updates.Decide(G(UpdateMode.Notify, skip: "v0.11.0"), new Version(0, 11, 1), Current, true, Noon));   // a newer one asks again
        Assert.Equal(Updates.Action.None, Updates.Decide(G(UpdateMode.Notify), new Version(0, 10, 1), Current, true, Noon));                    // already on it
    }

    [Fact]
    public void Auto_installs_on_the_installed_copy_and_waits_for_the_overnight_window_when_asked()
    {
        Assert.Equal(Updates.Action.Install, Updates.Decide(G(UpdateMode.Auto), Newer, Current, true, Noon));
        Assert.Equal(Updates.Action.Install, Updates.Decide(G(UpdateMode.Auto, skip: "v0.11.0"), Newer, Current, true, Noon));   // skip only silences the prompt
        Assert.Equal(Updates.Action.Wait, Updates.Decide(G(UpdateMode.Auto, overnight: true), Newer, Current, true, Noon));
        Assert.Equal(Updates.Action.Install, Updates.Decide(G(UpdateMode.Auto, overnight: true), Newer, Current, true, ThreeAm));
        Assert.Equal(Updates.Action.Notify, Updates.Decide(G(UpdateMode.Auto), Newer, Current, false, Noon));                    // a source build can't replace itself
    }

    [Fact]
    public void Off_never_acts() =>
        Assert.Equal(Updates.Action.None, Updates.Decide(G(UpdateMode.Off), Newer, Current, true, Noon));
}
