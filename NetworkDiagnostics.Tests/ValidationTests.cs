using System.Net;
using NetworkDiagnostics.Core.Interfaces;
using NetworkDiagnostics.Core.Models;
using NetworkDiagnostics.Core.Monitoring;

namespace NetworkDiagnostics.Tests;

public class TargetValidatorTests
{
    [Theory]
    [InlineData("8.8.8.8", "8.8.8.8", true)]
    [InlineData("  1.1.1.1 ", "1.1.1.1", true)]
    [InlineData("192.168.0.1", "192.168.0.1", true)]
    [InlineData("example.com", "example.com", false)]
    [InlineData("Dns.Google.", "dns.google", false)]
    [InlineData("localhost", "localhost", false)]
    [InlineData("my-router.lan", "my-router.lan", false)]
    public void Valid(string input, string normalized, bool isIp)
    {
        var r = TargetValidator.Validate(input);
        Assert.True(r.IsValid, r.Error);
        Assert.Equal(normalized, r.Normalized);
        Assert.Equal(isIp, r.IsIPv4Literal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("8.8.8")]          // IPAddress.TryParse would accept this as 8.8.0.8
    [InlineData("256.1.1.1")]
    [InlineData("01.2.3.4")]
    [InlineData("1.2.3.4.5")]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("224.0.0.1")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("http://example.com")]
    [InlineData("exa mple.com")]
    [InlineData("-bad.com")]
    [InlineData("under_score.com")]
    [InlineData("host.123")]
    public void Invalid(string? input)
    {
        var r = TargetValidator.Validate(input);
        Assert.False(r.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }
}

public class AdapterSelectionTests
{
    private readonly List<AdapterInfo> _adapters = [TestData.WiFi(), TestData.Ethernet()];

    [Fact]
    public void NullId_IsAutomatic_WithSystemDefault()
    {
        var r = AdapterSelector.Resolve(null, _adapters, "en5");
        Assert.True(r.Selection.IsAutomatic);
        Assert.Equal("en5", r.Selection.Adapter!.Id);
        Assert.Equal(IPAddress.Parse("10.0.0.1"), r.Selection.Gateway);
        Assert.Null(r.Warning);
    }

    [Fact]
    public void ExplicitId_SelectsThatAdapter()
    {
        var r = AdapterSelector.Resolve("en0", _adapters, "en5");
        Assert.False(r.Selection.IsAutomatic);
        Assert.Equal("en0", r.Selection.Adapter!.Id);
        Assert.Equal("en0", r.Selection.Id);
    }

    [Fact]
    public void MissingSavedAdapter_FallsBackToAutomatic_WithWarning()
    {
        var r = AdapterSelector.Resolve("en9", _adapters, "en0");
        Assert.True(r.Selection.IsAutomatic);
        Assert.NotNull(r.Warning);
    }

    [Fact]
    public void Automatic_WithNoDefaultRoute_HasNoAdapter()
    {
        var r = AdapterSelector.Resolve("automatic", _adapters, null);
        Assert.True(r.Selection.IsAutomatic);
        Assert.Null(r.Selection.Adapter);
    }

    [Fact]
    public void InternalTarget_FollowsGateway_UnlessOverridden()
    {
        var sel = new AdapterSelection(false, TestData.WiFi());
        Assert.Equal("192.168.1.1", AdapterSelector.EffectiveInternalTarget(sel, false, "10.9.9.9"));
        Assert.Equal("10.9.9.9", AdapterSelector.EffectiveInternalTarget(sel, true, " 10.9.9.9 "));
        var eth = new AdapterSelection(false, TestData.Ethernet());
        Assert.Equal("10.0.0.1", AdapterSelector.EffectiveInternalTarget(eth, false, null));
    }
}

public class SessionOptionsBuilderTests
{
    private static readonly FakeProbeFactory Factory = new(() => new FakeProbe(_ => ProbeReply.Ok(1)));

    private static AdapterSelection Explicit(AdapterInfo a) => new(false, a);

    [Fact]
    public void ValidSettings_ProduceOptions()
    {
        var v = SessionOptionsBuilder.Build(new AppSettings(), Explicit(TestData.WiFi()), Factory);
        Assert.True(v.IsValid, string.Join("; ", v.Errors));
        Assert.Equal("192.168.1.1", v.Options!.InternalTarget);
        Assert.True(v.Options.InternalTargetIsGateway);
        Assert.Equal("8.8.8.8", v.Options.ExternalTarget);
        Assert.Equal(TimeSpan.FromMinutes(10), v.Options.Duration);
        Assert.Equal(TimeSpan.FromSeconds(1), v.Options.Interval);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(30)]
    public void AllowedDurations(int minutes)
    {
        var v = SessionOptionsBuilder.Build(new AppSettings { DurationMinutes = minutes }, Explicit(TestData.WiFi()), Factory);
        Assert.Equal(TimeSpan.FromMinutes(minutes), v.Options!.Duration);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(60)]
    public void DisallowedDurations_AreRejected(int minutes)
    {
        var v = SessionOptionsBuilder.Build(new AppSettings { DurationMinutes = minutes }, Explicit(TestData.WiFi()), Factory);
        Assert.False(v.IsValid);
    }

    [Fact]
    public void MissingGateway_RequiresOverride()
    {
        var a = TestData.WiFi(gw: null);
        var v = SessionOptionsBuilder.Build(new AppSettings(), Explicit(a), Factory);
        Assert.False(v.IsValid);
        Assert.Contains(v.Errors, e => e.Contains("gateway", StringComparison.OrdinalIgnoreCase));

        var withOverride = SessionOptionsBuilder.Build(new AppSettings { UseInternalTargetOverride = true, InternalTargetOverride = "192.168.1.254" }, Explicit(a), Factory);
        Assert.True(withOverride.IsValid);
        Assert.Equal("192.168.1.254", withOverride.Options!.InternalTarget);
        Assert.False(withOverride.Options.InternalTargetIsGateway);
    }

    [Fact]
    public void DisconnectedAdapter_IsRejected()
    {
        var v = SessionOptionsBuilder.Build(new AppSettings(), Explicit(TestData.WiFi(up: false)), Factory);
        Assert.False(v.IsValid);
        Assert.Contains(v.Errors, e => e.Contains("disconnected"));
    }

    [Fact]
    public void AutomaticWithoutDefault_IsRejected()
    {
        var v = SessionOptionsBuilder.Build(new AppSettings(), new AdapterSelection(true, null), Factory);
        Assert.False(v.IsValid);
    }

    [Fact]
    public void InvalidTargets_AreReportedTogether()
    {
        var s = new AppSettings { ExternalTarget = "8.8.8", UseInternalTargetOverride = true, InternalTargetOverride = "not a host" };
        var v = SessionOptionsBuilder.Build(s, Explicit(TestData.WiFi()), Factory);
        Assert.False(v.IsValid);
        Assert.Contains(v.Errors, e => e.StartsWith("Internal target"));
        Assert.Contains(v.Errors, e => e.StartsWith("External target"));
    }

    [Theory]
    [InlineData(50, 2000)]
    [InlineData(1000, 50)]
    [InlineData(120_000, 2000)]
    public void OutOfRangeTiming_IsRejected(int interval, int timeout)
    {
        var v = SessionOptionsBuilder.Build(new AppSettings { PingIntervalMs = interval, PingTimeoutMs = timeout }, Explicit(TestData.WiFi()), Factory);
        Assert.False(v.IsValid);
    }

    [Fact]
    public void UnsupportedBinding_IsReported_NotSilentlyIgnored()
    {
        var factory = new FakeProbeFactory(() => new FakeProbe(_ => ProbeReply.Ok(1)), BindingCheck.Unsupported("Binding not supported here."));
        var v = SessionOptionsBuilder.Build(new AppSettings(), Explicit(TestData.WiFi()), factory);
        Assert.False(v.IsValid);
        Assert.Contains("Binding not supported here.", v.Errors);
    }
}
