using System.Net;
using NetworkDiagnostics.Core.Interfaces;
using NetworkDiagnostics.Core.Models;

namespace NetworkDiagnostics.Tests;

/// <summary>Deterministic probe: replies are produced by a script, optionally after a delay.</summary>
public sealed class FakeProbe : IPingProbe
{
    private readonly Func<int, ProbeReply> _script;
    private readonly TimeSpan _delay;
    private readonly SharedConcurrency? _shared;
    private int _calls;
    private int _inFlight;

    public FakeProbe(Func<int, ProbeReply> script, TimeSpan delay = default, SharedConcurrency? shared = null)
    {
        _script = script;
        _delay = delay;
        _shared = shared;
    }

    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Highest number of simultaneous SendAsync calls on this probe.</summary>
    public int MaxInFlight { get; private set; }

    public List<IPAddress> Destinations { get; } = [];

    public bool Disposed { get; private set; }

    public string RoutingDescription => "fake";

    public async Task<ProbeReply> SendAsync(IPAddress destination, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var n = Interlocked.Increment(ref _calls);
        var now = Interlocked.Increment(ref _inFlight);
        lock (Destinations)
        {
            Destinations.Add(destination);
            MaxInFlight = Math.Max(MaxInFlight, now);
        }

        _shared?.Enter();
        try
        {
            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, cancellationToken);
            }

            return _script(n);
        }
        finally
        {
            _shared?.Exit();
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Tracks concurrency across several probes.</summary>
public sealed class SharedConcurrency
{
    private int _current;
    private int _max;

    public int Max => Volatile.Read(ref _max);

    public void Enter()
    {
        var c = Interlocked.Increment(ref _current);
        int m;
        while (c > (m = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, c, m) != m)
        {
        }
    }

    public void Exit() => Interlocked.Decrement(ref _current);
}

public sealed class FakeProbeFactory : IPingProbeFactory
{
    private readonly Func<FakeProbe> _create;

    public FakeProbeFactory(Func<FakeProbe> create, BindingCheck? check = null)
    {
        _create = create;
        Check = check ?? BindingCheck.Supported("fake");
    }

    public BindingCheck Check { get; set; }

    public List<FakeProbe> Created { get; } = [];

    public BindingCheck CheckSupport(AdapterSelection selection) => Check;

    public IPingProbe Create(AdapterSelection selection)
    {
        if (!Check.IsSupported)
        {
            throw new NotSupportedException(Check.Message);
        }

        var p = _create();
        lock (Created)
        {
            Created.Add(p);
        }

        return p;
    }
}

public sealed class FakeResolver : IHostResolver
{
    private readonly Func<string, int, HostResolution> _resolve;
    private int _calls;

    public FakeResolver(Func<string, int, HostResolution>? resolve = null)
    {
        _resolve = resolve ?? ((host, _) => IPAddress.TryParse(host, out var a) ? new HostResolution(a, null) : new HostResolution(IPAddress.Parse("93.184.216.34"), null));
    }

    public int Calls => _calls;

    public Task<HostResolution> ResolveIPv4Async(string hostOrAddress, CancellationToken cancellationToken) =>
        Task.FromResult(_resolve(hostOrAddress, Interlocked.Increment(ref _calls)));
}

public sealed class FakeAdapterProvider : INetworkInterfaceProvider
{
    public List<AdapterInfo> Adapters { get; set; } = [];

    public string? DefaultId { get; set; }

    public IReadOnlyList<AdapterInfo> GetAdapters() => Adapters.ToList();

    public string? GetSystemDefaultAdapterId() => DefaultId;
}

public static class TestData
{
    public static AdapterInfo WiFi(bool up = true, string ip = "192.168.1.20", string? gw = "192.168.1.1") =>
        new("en0", "Wi-Fi (en0)", "en0", AdapterKind.WiFi, ip is null ? null : IPAddress.Parse(ip), gw is null ? null : IPAddress.Parse(gw), up, up ? "Up" : "Down", 14);

    public static AdapterInfo Ethernet(bool up = true, string? ip = "10.0.0.5", string? gw = "10.0.0.1") =>
        new("en5", "USB LAN (en5)", "en5", AdapterKind.Ethernet, ip is null ? null : IPAddress.Parse(ip), gw is null ? null : IPAddress.Parse(gw), up, up ? "Up" : "Down", 7);

    public static SessionOptions Options(TimeSpan? duration = null, TimeSpan? interval = null, TimeSpan? timeout = null, string external = "8.8.8.8") => new()
    {
        Adapter = new AdapterSelection(false, WiFi()),
        InternalTarget = "192.168.1.1",
        InternalTargetIsGateway = true,
        ExternalTarget = external,
        Duration = duration ?? TimeSpan.FromMilliseconds(500),
        Interval = interval ?? TimeSpan.FromMilliseconds(50),
        Timeout = timeout ?? TimeSpan.FromMilliseconds(200),
    };

    public static PingResult Result(ProbeStatus status, double? rtt = null, long seq = 1, TargetType type = TargetType.Internal, string? detail = null) => new()
    {
        SessionId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Sequence = seq,
        TimestampUtc = new DateTimeOffset(2026, 11, 17, 9, 30, 0, TimeSpan.Zero).AddSeconds(seq),
        ScheduledOffsetMs = (seq - 1) * 1000,
        SentOffsetMs = (seq - 1) * 1000 + 0.25,
        AdapterId = "en0",
        AdapterLocalIp = "192.168.1.20",
        TargetType = type,
        Target = type == TargetType.Internal ? "192.168.1.1" : "8.8.8.8",
        ResolvedIp = type == TargetType.Internal ? "192.168.1.1" : "8.8.8.8",
        RoundTripMs = rtt,
        Status = status,
        ErrorDetail = detail,
    };
}

/// <summary>Integration tests touch the real network and only run when ND_INTEGRATION=1.</summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ND_INTEGRATION") != "1")
        {
            Skip = "Integration test: set ND_INTEGRATION=1 to run against the real network.";
        }
    }
}
