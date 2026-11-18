using System.Net;
using NetworkDiagnostics.Core.Interfaces;
using NetworkDiagnostics.Core.Models;

namespace NetworkDiagnostics.Core.Monitoring;

/// <summary>
/// Polls adapter state during a session and records disconnects, reconnects, address changes and (in automatic
/// mode) changes of the system default adapter.
/// </summary>
public sealed class AdapterWatcher
{
    private readonly INetworkInterfaceProvider _provider;
    private readonly AdapterSelection _selection;
    private readonly TimeProvider _time;
    private readonly TimeSpan _pollInterval;
    private readonly Action<SessionEvent> _onEvent;

    private bool _connected;
    private bool _missing;
    private IPAddress? _address;
    private string? _defaultId;

    public AdapterWatcher(INetworkInterfaceProvider provider, AdapterSelection selection, TimeProvider time, TimeSpan pollInterval, Action<SessionEvent> onEvent)
    {
        _provider = provider;
        _selection = selection;
        _time = time;
        _pollInterval = pollInterval;
        _onEvent = onEvent;
        _address = selection.LocalAddress;
        _connected = selection.Adapter is { IsUp: true, IPv4Address: not null };
        _defaultId = selection.Adapter?.Id;
    }

    /// <summary>Current local IPv4 address of the watched adapter (updated on each poll).</summary>
    public IPAddress? CurrentAddress => Volatile.Read(ref _address);

    public async Task RunAsync(long sessionStartTimestamp, CancellationToken token)
    {
        using var timer = new PeriodicTimer(_pollInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try
                {
                    Poll(_time.GetElapsedTime(sessionStartTimestamp));
                }
                catch (Exception ex)
                {
                    Raise(_time.GetElapsedTime(sessionStartTimestamp), SessionEventKind.Warning, "Adapter status check failed: " + ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Performs a single status check. Public for deterministic tests.</summary>
    public void Poll(TimeSpan offset)
    {
        var adapters = _provider.GetAdapters();

        if (_selection.IsAutomatic)
        {
            var currentDefault = _provider.GetSystemDefaultAdapterId();
            if (!string.Equals(currentDefault, _defaultId, StringComparison.OrdinalIgnoreCase))
            {
                var name = currentDefault is null ? null : adapters.FirstOrDefault(a => a.Id == currentDefault)?.Name ?? currentDefault;
                Raise(offset, currentDefault is null ? SessionEventKind.AdapterDisconnected : SessionEventKind.DefaultAdapterChanged,
                    currentDefault is null
                        ? "No default network connection (default route lost)."
                        : $"System default adapter changed to {name}.");
                _defaultId = currentDefault;
            }

            var def = currentDefault is null ? null : adapters.FirstOrDefault(a => a.Id == currentDefault);
            Volatile.Write(ref _address, def?.IPv4Address);
            return;
        }

        var target = _selection.Adapter!;
        var current = adapters.FirstOrDefault(a => string.Equals(a.Id, target.Id, StringComparison.OrdinalIgnoreCase));
        if (current is null)
        {
            if (!_missing)
            {
                Raise(offset, SessionEventKind.AdapterMissing, $"Adapter {target.Name} is no longer present.");
                _missing = true;
                _connected = false;
            }

            Volatile.Write(ref _address, null);
            return;
        }

        _missing = false;
        var connected = current.IsUp && current.IPv4Address is not null;
        if (connected != _connected)
        {
            Raise(offset, connected ? SessionEventKind.AdapterReconnected : SessionEventKind.AdapterDisconnected,
                connected ? $"Adapter {current.Name} reconnected ({current.IPv4Address})." : $"Adapter {current.Name} disconnected ({current.StatusText}).");
            _connected = connected;
        }
        else if (connected && !Equals(current.IPv4Address, _address) && _address is not null)
        {
            Raise(offset, SessionEventKind.AdapterAddressChanged, $"Adapter {current.Name} IPv4 address changed from {_address} to {current.IPv4Address}.");
        }

        Volatile.Write(ref _address, current.IPv4Address);
    }

    private void Raise(TimeSpan offset, SessionEventKind kind, string message) =>
        _onEvent(new SessionEvent(_time.GetUtcNow(), offset.TotalMilliseconds, kind, message));
}
