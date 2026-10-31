# PresentMon

`PresentMon-2.6.0-x64.exe` is the Intel PresentMon 2.6.0 CLI from the local,
previously verified diagnostics bundle. It is copied to build/publish output.
SHA-256: `B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF`.

Upstream: https://github.com/GameTechDev/PresentMon/releases/tag/v2.6.0
License: `PresentMon-LICENSE.txt` (MIT).

The FPS reader launches its own ETW session and reads CSV on standard output.
It does not inject code into DayZ. ETW access can require an elevated launch.

Automatic connection detection uses Windows ETW network events through the
bundled Microsoft TraceEvent library. Wireshark, tshark, and Npcap are not
required. A private trace session runs for three seconds every six seconds,
keeps IPv4 UDP endpoint metadata for the DayZ PID, and selects a sustained
bidirectional flow. No packet payloads or trace files are recorded. The adapter
is resolved from the local source IP. Both network ETW and PresentMon run in a
separate elevated helper process. The normal Companion UI remains unelevated.

The helper is the same app executable launched with `--performance-worker`,
before the UI singleton/HTTP service/settings initialization. It only receives a
query-port configuration and reports telemetry through a uniquely named local
pipe. The pipe ACL permits the initiating user and administrators, denies
network access, and both ends validate their peer PID using Windows APIs. The
helper additionally validates its parent's executable and start time, uses
anonymous impersonation on the client end, and restricts collection to the
parent's Windows session. It does not load the administrator's Companion or
DayZ settings. Messages have a 64 KiB maximum and a fixed protocol version.

The measurements checkbox starts the helper immediately. For a protected
Program Files installation, first use requests UAC consent to register a hidden,
demand-start scheduled task. The task has no triggers and runs only the fixed
Companion worker action; its owning user can read/run but cannot modify it.
Subsequent Companion starts run that task without another UAC prompt when the
saved measurements setting is enabled. Unchecking stops collection; the dormant
task remains available for the next enable and is removed by the uninstaller.
The executable and its ancestor directories are checked for unsafe ownership,
write permissions, and reparse points before persistent elevation is enabled.
Writable portable/development copies instead require UAC for each helper launch.
If consent uses another administrator account, the task uses SYSTEM and still
restricts collection to the initiating parent's Windows session. Cancellation or
startup failure turns the checkbox off. Closing the pipe or losing the parent
stops collection, waits for the private ETW sessions/PresentMon to stop, and exits
the helper. No Windows service is installed.

Query discovery uses Steam `ISteamApps/GetServersAtAddress` (one lookup on
discovery, with at least 60 seconds between unsuccessful attempts), matches the
game port, and validates a DayZ A2S_INFO response. Conventional fallback ports
are limited to game port + 1, game port + 3, 27016, 27015. Automatic discovery
requires either an exact Steam game-port mapping or the reply's EDF game port
to match the captured game port; a contradictory EDF port is always rejected.
An explicit manual override is also available. A successful endpoint
is cached for that connection. Normal polling is one second, slowing down on
repeated timeouts. Failed samples display a dash.

Query requests bind to the captured local source IP. Per-application VPN routing
can still differ between Companion and DayZ. The displayed ping is query RTT,
not DayZ's internal session latency.

Optional frame metrics use the dominant current swap chain. Frame time is the
mean over the last second. 1% low is 1000 divided by the mean of the slowest 1%
of frame intervals in a rolling 30-second window, after at least 100 frames.
The 60-second graph retains the highest frame interval seen in each second and
leaves gaps when no fresh frames are available. Query jitter is the mean absolute
difference between adjacent successful RTT samples in a 30-second window;
timeouts and connection changes reset its history.

The performance overlay uses a per-pixel alpha bitmap for separate background
and text opacity. Legacy `Opacity` values initialize both new opacity settings.
Threshold coloring is optional and applies to FPS/1% low (lower is worse) and
ping (higher is worse). The graph uses the equivalent FPS thresholds for its
frame-time peaks.
