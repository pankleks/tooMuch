# tooMuch

> **Work in progress** — this project is under active development. Features, configuration, and usage may change.

## Windows service architecture

One automatic LocalSystem service, `TooMuch`, owns policy, usage and enforcement.
The old `TooMuch` / `TooMuchTray` scheduled tasks are removed during migration.
This version has no tray or fullscreen overlays. Closing a user application cannot
stop accounting or enforcement.

### Messages and time warnings

The admin panel's **Send message** button queues a message for the child's unlocked
session (up to 1000 characters, default expiry 15 minutes). Statuses distinguish
pending, delivered to the client, confirmed with Windows' OK button, and expired.
Delivered does not prove the dialog was seen; confirmation does not prove it was read.
The service polls messages every 10 seconds while the child is active and not blocked.
Dialogs use `WTSSendMessage` on background tasks, so waiting for a response never
pauses accounting or enforcement. A dialog times out after at most 60 seconds;
timeout is not confirmation. Unconfirmed dialogs are not repeatedly shown during
the same service run, but may be retried after a restart until expiry.

The service also warns once when at most 10 minutes remain, using actual remaining
time if a limit was shortened. Daily limits use usage; allowed windows use their end
time. Adjacent windows are treated as continuous. Automatic warnings work offline
from cached policy; shown warning keys are persisted to avoid repeats after restart.
Windows provides the standard OK button; no custom tray UI is required.

The protected account is recorded by SID. Installation under UAC discovers the
owner of Explorer in the installer's interactive session, rather than the admin
credentials used for elevation. Ambiguous discovery requires
`-ChildAccount 'COMPUTER\child'`. A saved SID is reused during upgrades.

Time is counted once while at least one session of that SID is active and unlocked,
including watching films without keyboard/mouse input. Locked/disconnected sessions,
sleep and other accounts do not count. The service persists fractional minutes;
unknown gaps longer than five seconds are not charged. Local cached policy applies
offline; missing policy fails closed. Limits use the server's `TIME_ZONE` setting
(default `Europe/Warsaw`). Set that environment variable and restart the server to change it.

At the limit the service uses `WTSDisconnectSession`: applications remain running,
but the child returns to sign-in. Reconnection is checked every second and disconnected
again while blocked. This does not prevent entering credentials or provide a Windows
logon prohibition. The protected account must be a standard user, not administrator.

### Install / upgrade (elevated PowerShell, from the new bundle)

```powershell
.\install.ps1 -ServerUrl http://192.168.70.29:3020
Get-Service TooMuch
```

Existing identity, SID and usage in ProgramData are reused; no uninstall is needed.
If changing servers, supply both `-DeviceId` and `-Token`. The installer stops legacy
launchers before replacing the executable. Local mutable data and credentials are
restricted to SYSTEM and administrators. Uninstall retains these data for reinstalls.

Client failures are recorded in the Windows Application event log (source TooMuch).
An SCM `Running` state and a server heartbeat alone do not prove enforcement works.

### Windows acceptance checks before deployment

Run on a test machine with a standard child account and a separate administrator:

1. Elevate from the child's desktop using parent credentials; verify the printed SID.
2. Use the child desktop for two minutes without input (e.g. video); usage must increase.
3. Lock the workstation, switch to the parent, and suspend/resume; those intervals must not count.
4. Set a short limit or force-lock; verify return to sign-in and re-disconnection after reconnecting.
5. Verify the parent's session stays accessible, including with two monitors attached.
6. Reboot, restart the service and upgrade in place; verify identity and usage survive.
7. Disconnect the network; cached limits must still apply. Reconnect and check usage uploads.

Unit tests cover accounting intervals, persistence, timezone dates and policy evaluation.
Native Windows session APIs, UAC account discovery, ACLs and SCM migration require the
interactive acceptance checks above; they are not established by the unit tests.

The file-based server supports one process per data directory. Mutating HTTP requests
are serialized to prevent heartbeats overwriting configuration; do not share this
directory between server replicas.
