# Network Diagnostics

A small desktop app (C#, .NET 10, Avalonia 12) that measures latency and packet loss to two destinations at the same time:

- **Internal**: the default gateway (router) of the selected network adapter.
- **External**: a configurable internet host (default `8.8.8.8`).

Results update live and can be exported as a ZIP for later analysis. There is no server, account, database or telemetry.

## Using the app

1. Choose a network adapter, or leave **Automatic (System Default)**. The panel shows the adapter's type, local IPv4 address, gateway, connection status, and how probes will be routed.
2. Check the targets. The internal target is filled in from the adapter's gateway. To enter a different one, open **Advanced settings → Use a manual internal target**. The external target accepts an IPv4 address or a hostname.
3. Pick a duration (1, 5, 10 or 30 minutes) and press **Start diagnostics**.
4. Watch both panels: latest, average, min and max latency; sent, received and lost counts; loss %; a traffic-light status; and a latency graph. Failed probes show as red bands on the graph, never as 0 ms.
5. Press **Stop diagnostics** at any time. Results collected so far are kept.
6. Press **Export results…** to save the ZIP.

### Traffic-light status

These statuses are preliminary measurement statuses, not a diagnosis. Thresholds are defined in `StatusThresholds` and stored in the settings file. Each status is based on the last 20 completed probes:

| Status | Rule (first match wins) |
|---|---|
| 🔴 **Not responding** | 3 or more consecutive failures |
| 🔴 **Heavy packet loss** | ≥ 20 % of the window failed (needs ≥ 5 samples) |
| 🟡 **Intermittent failures** | any failure in the window |
| 🟡 **Elevated latency** | window average above 30 ms (internal) / 100 ms (external) |
| 🟢 **Responding normally** | otherwise |

Each colour is always shown with its text label.

### Export contents

| File | Contents |
|---|---|
| `session.json` | session ID, app version, start/end (UTC), requested and actual duration, end reason, adapter (selection mode, name, type, IPv4, gateway, routing method), targets, interval, timeout, thresholds, platform, and an event log (adapter disconnects and address changes, DNS resolution, warnings) |
| `internal-ping.csv`, `external-ping.csv` | one row per probe: `session_id, sequence, timestamp_utc, scheduled_offset_ms, sent_offset_ms, adapter_id, adapter_local_ip, target_type, target, resolved_ip, rtt_ms, status, error_detail` |
| `summary.json` | per target: sent, completed, received, failed, cancelled, loss %, min/avg/max, and counts per status |

Formatting:
- Numbers use invariant culture (`.` decimal separator).
- Timestamps are ISO 8601 UTC (`2026-11-18T01:11:21.990Z`).
- Failed rows keep their status, and their `rtt_ms` is empty.

The export does not include the machine name, user name, MAC addresses or credentials. It does include the adapter's local IPv4 address and gateway, because the spec requires them.

`status` is one of: `Success`, `Timeout`, `DestinationUnreachable`, `TtlExpired`, `LocalAdapterFailure`, `DnsFailure`, `PermissionDenied`, `Cancelled`, `Error`.

A sample report from a real 20-second session (macOS, Wi-Fi) is in [docs/sample-report/](docs/sample-report/).

## How measurements work

- **Scheduling.** Each target has its own monitor, and the two run concurrently. Probes are planned on a fixed grid: slot × interval, measured from session start with a monotonic clock. Only one probe per target is in flight at a time. If a reply takes longer than the interval, the missed slots are **skipped, not queued**, so there is never a backlog or a burst.
- **Latency.** RTT is measured with a monotonic stopwatch around the send and receive. It includes a small amount of user-space overhead (typically well under 0.5 ms).
- **Statistics.** Packet loss = `failed / completed × 100`.
  - In-flight probes are not "completed".
  - A probe abandoned by **Stop** is recorded as `Cancelled` and excluded from loss and latency.
  - Latency min, average and max use successful replies only.
- **Hostnames.** Names are resolved to IPv4 once, by the operating-system resolver. If resolution fails, each attempt is recorded as `DnsFailure` and retried at the next slot. Once a name resolves, that address is used for the rest of the session.
- **Adapter watcher.** The selected adapter is polled every 2 s. Disconnects, reconnects, removal, address changes, and (in Automatic mode) changes of the system default adapter are recorded as session events.

### Adapter binding (how "use this adapter" is honoured)

The .NET `Ping` class cannot choose an interface, so explicit selection uses platform-specific code. No routing tables are modified.

| Platform | Explicit adapter | Automatic |
|---|---|---|
| **macOS** | Unprivileged ICMP datagram socket (`SOCK_DGRAM`/`IPPROTO_ICMP`) with `IP_BOUND_IF`, so the kernel does a scoped route lookup on that interface only. Verified on macOS 26 / Apple Silicon. | `System.Net.NetworkInformation.Ping` |
| **Windows** | `IcmpSendEcho2Ex` (iphlpapi) with the adapter's IPv4 as the source address. Windows' default *strong host* send model sends it out the interface that owns that address. No elevation needed. | `System.Net.NetworkInformation.Ping` |
| Linux / other | Reported as unsupported. The app asks you to choose Automatic. | `Ping` |

If binding isn't possible (for example, the interface has gone or ICMP sockets are refused), the **Routing** line turns red and Start reports the reason. The app never quietly falls back to a different interface. If a saved adapter is missing at startup, the app switches to Automatic and shows a notice. If the selected adapter disappears just before Start, Start is refused.

## Build and run

You need the .NET 10 SDK.

```bash
dotnet build
dotnet run --project NetworkDiagnostics.App
```

Settings are saved to `%LOCALAPPDATA%\NetworkDiagnostics\settings.json` (Windows) or `~/Library/Application Support/NetworkDiagnostics/settings.json` (macOS). The file is written when a test starts and when the app closes. A missing, corrupt or out-of-range file falls back to defaults.

## Tests

```bash
dotnet test                       # unit tests: mocked probes, no network needed
```

Unit tests cover:
- packet-loss, min, average and max calculations
- timeout handling
- concurrent monitoring
- cancellation and Stop
- duration handling and the no-backlog scheduler
- configuration serialization and sanitising
- CSV escaping and invariant formatting
- the ZIP layout
- adapter selection
- invalid configuration
- ICMP packet parsing
- the traffic-light rules
- the view model's Start → Stop → Export flow

### Integration tests (real network)

These are skipped unless you opt in.

```bash
ND_INTEGRATION=1 dotnet test --filter Category=Integration
# also write a sample report from a real 20-second session:
ND_INTEGRATION=1 ND_SAMPLE_REPORT="$PWD/docs/sample-report" dotnet test --filter Category=Integration
```

They check that:
- adapters are enumerated, and the default adapter has a gateway
- an explicitly bound probe reaches the gateway
- an Automatic probe reaches the gateway
- a probe to TEST-NET-1 (`192.0.2.1`) times out rather than reporting 0 ms
- a 20-second session completes with the expected number of probes

**Manual checks on each platform** (run the published app, not `dotnet run`; see the macOS note below):

1. Confirm the adapter list matches the system's network settings, and that the gateway matches `route -n get default` (macOS) or `ipconfig` (Windows).
2. With Wi-Fi and Ethernet both connected, select the **non-default** adapter and run a 1-minute test. On a second terminal, confirm the traffic leaves that interface:
   - macOS: `sudo tcpdump -ni en5 icmp`
   - Windows: Wireshark, or `pktmon start --capture` followed by `pktmon stop`.
3. Unplug the cable or turn Wi-Fi off during a test. Check that failures are recorded (not 0 ms), the status turns red, and a disconnect event appears under **Session events**.
4. Set the external target to an invalid value (`8.8.8`), and to a name that doesn't exist (`nonexistent.invalid`). The first should be rejected before start; the second should be recorded as `DnsFailure`.
5. Press Stop mid-test, export, and open the ZIP.

## Publishing (self-contained)

```bash
# macOS: produces artifacts/<rid>/Network Diagnostics.app (ad-hoc signed)
scripts/publish-macos.sh osx-arm64      # Apple Silicon
scripts/publish-macos.sh osx-x64        # Intel

# Windows: produces artifacts/publish/<rid>/NetworkDiagnostics.exe (single file)
pwsh scripts/publish-windows.ps1 -Rid win-x64
# or, from any OS:
dotnet publish NetworkDiagnostics.App -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/publish/win-x64
```

The ad-hoc signature is enough to run the app on the Mac that built it. To distribute it to other Macs, sign it with a Developer ID certificate (`codesign --options runtime`) and notarize it (`xcrun notarytool`). Windows builds are unsigned, so SmartScreen may warn on first run.

## Known limitations

- **macOS Local Network permission (important).** On macOS 15 and later, an app bundle needs the user's permission to reach devices on the local network, and **packets to the router are silently dropped until it is granted**. Without the permission the router looks dead: 100 % internal loss while the internet works. To deal with this:
  - The app sends one echo to the gateway at startup, so the permission prompt appears before a test.
  - The bundle's `Info.plist` explains why the permission is needed.
  - If the internal target never replies while the external one does, the app shows a banner pointing to *System Settings → Privacy & Security → Local Network* and records a warning in the session events.

  `dotnet run` inherits the terminal's permission, so this only appears with the published `.app`.
- **Windows binding has not been run on Windows.** The Windows code compiles, and a self-contained `win-x64` build is produced, but it has not been executed on a Windows machine. Source-address binding relies on the strong host send model; an interface with `weakhostsend` enabled (`netsh interface ipv4 show interface <n>`) could route differently. Windows Firewall rules that block outbound ICMP will show up as timeouts.
- **Non-default adapter gateways on macOS** come from .NET's routing-table query. This was verified for the primary adapter only, not for a secondary one.
- **DNS resolution** uses the system resolver and is not bound to the selected adapter.
- **IPv4 only.** IPv6 targets are rejected.
- Adapters without an IPv4 address are not listed, because they cannot be tested.
- If an explicitly selected Windows adapter changes its IPv4 address mid-session, later probes fail with `LocalAdapterFailure` and an address-change event is recorded. Restart the test to use the new address.
- Statuses are simple threshold rules, not a diagnosis. Some routers deliberately de-prioritise ICMP, which inflates gateway latency.

## Project layout

```text
NetworkDiagnostics.Core/      UI-free engine (net10.0)
  Models/                     AdapterInfo, PingResult, LatencyStatistics, AppSettings, DiagnosticSession, …
  Interfaces/                 INetworkInterfaceProvider, IPingProbe(Factory), IHostResolver, ISettingsStore
  Monitoring/                 PingMonitor, DiagnosticSessionRunner, AdapterWatcher, ProbeScheduler,
                              TargetValidator, AdapterSelector, SessionOptionsBuilder, IcmpPacket, …
  Statistics/                 StatisticsAccumulator, HealthEvaluator
  Export/                     PingCsvWriter, SessionReportExporter, JsonDefaults
  Configuration/              JsonSettingsStore
NetworkDiagnostics.App/       Avalonia UI (MVVM, CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection)
  Views/ ViewModels/ Models/ Controls/LatencyGraph.cs Services/ Configuration/ServiceRegistration.cs
  Platforms/                  SystemNetworkInterfaceProvider, SystemPingProbe, PlatformPingProbeFactory
  Platforms/MacOS/            MacBoundIcmpProbe (IP_BOUND_IF), MacHardwarePortMap (networksetup)
  Platforms/Windows/          WindowsSourceBoundIcmpProbe (IcmpSendEcho2Ex)
NetworkDiagnostics.Tests/     xUnit tests and opt-in integration tests
scripts/                      publish-macos.sh, publish-windows.ps1
docs/sample-report/           real exported report
```

Third-party packages: Avalonia (UI), CommunityToolkit.Mvvm, and Microsoft.Extensions.DependencyInjection. The chart is a custom control, so no charting library is used.
