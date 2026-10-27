# creaform_bridge protocol

Protocol version: **2**. This file is the single source of truth for the messages between `creaform_bridge`
(Windows) and `creaform_hdlr` (ROS 2, platform repo). Bump `BridgeInfo.ProtocolVersion` and the node's
`expected_protocol_version` parameter together whenever this file changes in a way the other side must know about.

## Framing

UTF-8 JSON objects over TCP, one per line, terminated by `\n`.

- Request: `{"id": 1, "cmd": "readiness"}`. The `id` is optional and is echoed back in the reply.
- Reply: `{"id": 1, "ok": false, "code": "NOT_READY", "message": "...", "data": {...}}`. `data` is optional.
- Event (unsolicited, no `id`): `{"event": "disconnected"}`, sent to every client when Creaform.OS reports the
  API connection was lost. This includes the loss caused by `shutdown`.

## Commands

| Command | Behavior | Codes |
| --- | --- | --- |
| `ping` | Liveness check, also the heartbeat. Not logged by the bridge. | `OK` |
| `status` | `data`: `bridgeVersion`, `protocolVersion`, `installed`, `connected`, `shutdownInProgress`. | `OK`, `INTERNAL_ERROR` |
| `connect` | `ApiManager.Connect`, which attaches to a running Creaform.OS or starts one. Waits for a previous `shutdown` to finish first. Makes the sender a **controller**. | `CONNECTED`, `ALREADY_CONNECTED` (both `ok: true`), `NOT_INSTALLED`, `SHUTDOWN_IN_PROGRESS`, `CONNECT_FAILED`, exception codes, `INTERNAL_ERROR` |
| `readiness` | One readiness pass, see below. | `READY` (`ok: true`), `NOT_READY` |
| `shutdown` | Calls `Application.Exit()`, which closes the CreaformAPI process and Creaform.OS. Replies as soon as `Exit()` is sent; the bridge waits for the close in the background (up to `shutdownWaitS`). The sender stops being a controller. | `SHUTDOWN_STARTED`, `SHUTDOWN_IN_PROGRESS`, `NOT_CONNECTED` (all `ok: true`), exception codes, `INTERNAL_ERROR` |

Other replies: `BAD_REQUEST`, `UNKNOWN_COMMAND`.

Exception codes (from `CreaformApi.Exception.ExceptionCause`): `NOT_INSTALLED`, `NOT_STARTED`, `NOT_CONNECTED`,
`CLIENT_VERSION_INCOMPATIBLE`, `CONNECTION_FAILED`, `CREAFORM_ERROR` (any other cause).

## Controllers and client loss

A client that sends `connect` is a controller. A controller is lost when its socket closes without a `shutdown`, or
when it sends nothing for `clientIdleS` seconds (0 turns this off). Controllers should send `ping` more often than
that. When the last controller is lost, the bridge waits `clientLossShutdownS` seconds; if no client sends
`connect` in that time, it runs `shutdown` itself. Stopping the bridge with Ctrl+C also runs `shutdown`.

## Readiness

`data` holds `faults` (a list) and `info` (an object). `ok` is true and `code` is `READY` only when `faults` is empty.

Each fault: `{"code", "category", "retryable", "message"}`. `retryable: true` means the fault can clear without
anyone acting (warming, synchronizing, scanner coming into view), so the caller may poll again. Categories:
`connection`, `version`, `module`, `calibration`, `transient`, `hardware`, `session`, `bridge`.

Checks run in this order. A failed gate ends the pass.

| # | Check | Fault codes | Category | Retryable |
| --- | --- | --- | --- | --- |
| 0 gate | No `shutdown` in progress | `SHUTDOWN_IN_PROGRESS` | connection | yes |
| 1 gate | `ApiManager.IsConnected` | `NOT_CONNECTED` | connection | no |
| 2 gate | `ExecutingApiVersion == expectedExecutingApiVersion` | `VERSION_NOT_PINNED`, `VERSION_MISMATCH` | version | no |
| 3 gate | `IsScanningSupported` | `SCANNING_NOT_SUPPORTED` | module | no |
| 4 gate | No scan running, session file exists, then `LoadSession` | `SCAN_IN_PROGRESS`, `SESSION_MISSING`, `SESSION_LOAD_FAILED` | session | yes, no, no |
| 5 | `Tracker.TrackerOperatingStatus` | `TRACKER_CALIB_TOO_OLD`, `TRACKER_CALIB_REQUIRED`, `TRACKER_CALIB_REQUIRED_TOO_HOT`, `TRACKER_CALIB_REQUIRED_TOO_COLD` | calibration | no |
| 5 | | `TRACKER_WARMING`, `TRACKER_TEMP_OUT_OF_RANGE`, `TRACKER_NOT_SYNCHRONIZED`, `TRACKER_SYNCHRONIZING` | transient | yes |
| 5 | | `TRACKER_NOT_REFERENCED`, `TRACKER_DAMAGED`, `TRACKER_UNKNOWN` | hardware | no |
| 5 | Tracker module missing while `IsTrackerRequiredToScan` | `TRACKER_UNAVAILABLE` | hardware | no |
| 6 | Status `Ready` but `Tracker.IsReady` false (when the tracker is required) | `TRACKER_NOT_READY` | hardware | yes |
| 7 | `IsSoftwareTriggerSupported`, `IsScannerConfigurationAvailable()` | `SCANNER_NO_SW_TRIGGER`, `SCANNER_NO_CONFIG` | module | no |
| 7 | Scan module handle missing | `SCANNER_UNAVAILABLE` | hardware | no |
| 8 | `IsScannerReady`, `IsReadyToScan` | `SCANNER_NOT_READY`, `SCANNER_NOT_READY_TO_SCAN` | hardware | yes |
| 9 | `IsScanningStarted` is false | `SCAN_IN_PROGRESS` | session | yes |

Step 4 loads the session once per Creaform.OS run: a later pass with the same `sessionPath` skips the load. It runs
before the hardware checks because loading changes the tracker and scanner state the API reports, so `READY` always
describes the loaded session. The bridge never saves the session.

A Creaform exception during the pass is reported as a fault with the exception code and category `connection`.
Any other exception is `INTERNAL_ERROR`, category `bridge`.

`info` fields (present when the pass got far enough to read them): `apiVersion`, `executingApiVersion`,
`expectedExecutingApiVersion`, `isStatusWindowVisible`, `isScanningSupported`, `isTrackingSupported`,
`isShootingSupported`, `isProbingSupported`, `isTrackerRequiredToScan`, `trackerStatus`, `trackerStatusText` and
`trackerStatusColor` (what the Creaform.OS status window shows), `trackerIsReady`, `trackerReferencingMode`,
`lastCalibrationDate`,
`isSoftwareTriggerSupported`, `isScannerConfigurationAvailable`, `isScannerReady`, `isReadyToScan`,
`isScanningStarted`, `sessionPath`, `sessionLoaded`.

`isTrackingSupported` is the DynamicTracking module, not the C-Track, so it is information only. Module licenses
cannot be queried up front; a missing license shows up as `SCANNING_NOT_SUPPORTED` or later as `NoLicenseFound`
from `StartScanSync`.
