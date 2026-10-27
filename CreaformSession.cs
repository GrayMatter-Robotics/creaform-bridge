using CreaformApi.Tracker;
using CreaformAutomationApi;

namespace CreaformBridge
{
    /// <summary>Outcome of a session call. Code is a short machine-readable tag, Message is for people.</summary>
    public sealed record Result(bool Ok, string Code, string Message, IReadOnlyDictionary<string, object?>? Data = null);

    /// <summary>
    /// Owns the connection to Creaform.OS. Only this class touches the Creaform API, so the network
    /// layer never needs to know about it. One lock protects the connection state because the TCP
    /// threads, the client-loss timer and the Creaform event threads can reach it at the same time.
    /// </summary>
    public sealed class CreaformSession
    {
        private readonly object mLock = new object();
        private readonly BridgeConfig mConfig;
        private bool mDisconnectHandlerAttached;
        private string? mLoadedSessionPath;

        // Set while no Exit() is in progress. Connect waits on it, because the API docs say a new
        // connection must wait until the previous instance has fully exited.
        private readonly ManualResetEventSlim mExitFinished = new ManualResetEventSlim(true);
        private readonly ManualResetEventSlim mApiDisconnected = new ManualResetEventSlim(false);

        /// <summary>Raised (outside the lock) when Creaform.OS reports that the connection was lost.</summary>
        public event Action? Disconnected;

        public CreaformSession(BridgeConfig pConfig)
        {
            mConfig = pConfig;
        }

        private TimeSpan ShutdownWait => TimeSpan.FromSeconds(mConfig.ShutdownWaitS);

        public Result Ping()
        {
            return new Result(true, "OK", "pong");
        }

        public Result GetStatus()
        {
            try
            {
                var lData = new Dictionary<string, object?>
                {
                    ["bridgeVersion"] = BridgeInfo.Version,
                    ["protocolVersion"] = BridgeInfo.ProtocolVersion,
                    ["installed"] = ApiManager.IsInstalled,
                    ["connected"] = ApiManager.IsConnected,
                    ["shutdownInProgress"] = !mExitFinished.IsSet,
                };
                return new Result(true, "OK", "status", lData);
            }
            catch (System.Exception ex)
            {
                return new Result(false, "INTERNAL_ERROR", ex.Message);
            }
        }

        /// <summary>
        /// Connects to Creaform.OS. Per the API documentation, ApiManager.Connect attaches to a running
        /// instance or starts a new one if none is running.
        /// </summary>
        public Result Connect()
        {
            if (!mExitFinished.Wait(ShutdownWait))
            {
                return new Result(false, "SHUTDOWN_IN_PROGRESS", "Creaform.OS is still closing after Exit(); try again");
            }

            lock (mLock)
            {
                try
                {
                    if (!ApiManager.IsInstalled)
                    {
                        return new Result(false, "NOT_INSTALLED", "Creaform.OS is not installed on this computer");
                    }

                    if (ApiManager.IsConnected)
                    {
                        AttachDisconnectHandler();
                        return new Result(true, "ALREADY_CONNECTED", "Already connected to Creaform.OS");
                    }

                    mApiDisconnected.Reset();
                    mLoadedSessionPath = null;
                    ApiManager.Connect(BuildConnectParameters());

                    if (!ApiManager.IsConnected)
                    {
                        return new Result(false, "CONNECT_FAILED", "ApiManager.Connect returned but the manager is not connected");
                    }

                    AttachDisconnectHandler();
                    return new Result(true, "CONNECTED", "Connected to Creaform.OS");
                }
                catch (CreaformApi.Exception ex)
                {
                    return new Result(false, CauseCode(ex.ExceptionCause), ex.Message);
                }
                catch (System.Exception ex)
                {
                    return new Result(false, "INTERNAL_ERROR", ex.Message);
                }
            }
        }

        /// <summary>
        /// Closes the CreaformAPI process and Creaform.OS with Application.Exit(). Returns once Exit() is sent;
        /// a background task waits for Creaform.OS to close so callers on a short shutdown timer are not held up.
        /// </summary>
        public Result Shutdown()
        {
            lock (mLock)
            {
                if (!mExitFinished.IsSet)
                {
                    return new Result(true, "SHUTDOWN_IN_PROGRESS", "Creaform.OS is already closing");
                }

                try
                {
                    if (!ApiManager.IsConnected)
                    {
                        return new Result(true, "NOT_CONNECTED", "Not connected to Creaform.OS; nothing to close");
                    }

                    mExitFinished.Reset();
                    mApiDisconnected.Reset();
                    mLoadedSessionPath = null;
                    ApiManager.Application.Exit();
                }
                catch (CreaformApi.Exception ex)
                {
                    mExitFinished.Set();
                    return new Result(false, CauseCode(ex.ExceptionCause), ex.Message);
                }
                catch (System.Exception ex)
                {
                    mExitFinished.Set();
                    return new Result(false, "INTERNAL_ERROR", ex.Message);
                }
            }

            Console.WriteLine("[bridge] Exit() sent, waiting for Creaform.OS to close");
            _ = Task.Run(WaitForExit);
            return new Result(true, "SHUTDOWN_STARTED", "Exit() sent; Creaform.OS is closing");
        }

        /// <summary>Blocks until a running Shutdown() has finished or the timeout passes.</summary>
        public bool WaitForShutdown(TimeSpan pTimeout)
        {
            return mExitFinished.Wait(pTimeout);
        }

        /// <summary>
        /// One readiness pass. Gate checks stop the pass when they fail; the others are all reported.
        /// Each fault says whether it can clear on its own (retryable) so the caller can decide to wait.
        /// </summary>
        public Result Readiness()
        {
            var lFaults = new List<Dictionary<string, object?>>();
            var lInfo = new Dictionary<string, object?>();

            lock (mLock)
            {
                try
                {
                    RunReadinessChecks(lFaults, lInfo);
                }
                catch (CreaformApi.Exception ex)
                {
                    lFaults.Add(Fault(CauseCode(ex.ExceptionCause), "connection", ex.Message, false));
                }
                catch (System.Exception ex)
                {
                    lFaults.Add(Fault("INTERNAL_ERROR", "bridge", ex.Message, false));
                }
            }

            var lData = new Dictionary<string, object?> { ["faults"] = lFaults, ["info"] = lInfo };
            if (lFaults.Count == 0)
            {
                return new Result(true, "READY", "Creaform.OS is ready to scan", lData);
            }
            string lCodes = string.Join(", ", lFaults.Select(f => f["code"]));
            return new Result(false, "NOT_READY", $"{lFaults.Count} readiness fault(s): {lCodes}", lData);
        }

        // Caller must hold mLock.
        private void RunReadinessChecks(List<Dictionary<string, object?>> pFaults, Dictionary<string, object?> pInfo)
        {
            if (!mExitFinished.IsSet)
            {
                pFaults.Add(Fault("SHUTDOWN_IN_PROGRESS", "connection", "Creaform.OS is closing after Exit()", true));
                return;
            }

            // 1. Connection (gate)
            if (!ApiManager.IsConnected)
            {
                pFaults.Add(Fault("NOT_CONNECTED", "connection", "Not connected to Creaform.OS; send connect first", false));
                return;
            }

            IApplicationAutomation lApp = ApiManager.Application;
            int lExecuting = lApp.ExecutingApiVersion;
            pInfo["apiVersion"] = CreaformApi.IApplication.ApiVersion;
            pInfo["executingApiVersion"] = lExecuting;
            pInfo["expectedExecutingApiVersion"] = mConfig.ExpectedExecutingApiVersion;
            pInfo["isStatusWindowVisible"] = lApp.IsStatusWindowVisible;
            pInfo["isScanningSupported"] = lApp.IsScanningSupported;
            pInfo["isTrackingSupported"] = lApp.IsTrackingSupported;
            pInfo["isShootingSupported"] = lApp.IsShootingSupported;
            pInfo["isProbingSupported"] = lApp.IsProbingSupported;

            // 2. Version pin (gate)
            if (mConfig.ExpectedExecutingApiVersion == 0)
            {
                pFaults.Add(Fault("VERSION_NOT_PINNED", "version",
                    $"expectedExecutingApiVersion is not set in bridge.config.json; Creaform.OS reports {lExecuting}", false));
                return;
            }
            if (lExecuting != mConfig.ExpectedExecutingApiVersion)
            {
                pFaults.Add(Fault("VERSION_MISMATCH", "version",
                    $"Creaform.OS API version is {lExecuting}, bridge.config.json expects {mConfig.ExpectedExecutingApiVersion}", false));
                return;
            }

            // 3. Scanning module (gate)
            if (!lApp.IsScanningSupported)
            {
                pFaults.Add(Fault("SCANNING_NOT_SUPPORTED", "module",
                    "The scanning module is not available in this session (scanner not detected or not licensed)", false));
                return;
            }

            var lScan = lApp.Scan;

            // 4. Persistent session, loaded once per Creaform.OS run. Loading changes the tracker and
            //    scanner state the API reports, so it runs before the hardware checks below.
            string lSessionPath = mConfig.ResolveSessionPath();
            pInfo["sessionPath"] = lSessionPath;
            if (mLoadedSessionPath != lSessionPath)
            {
                pInfo["sessionLoaded"] = false;
                if (lScan != null && lScan.IsScanningStarted)
                {
                    pFaults.Add(Fault("SCAN_IN_PROGRESS", "session", "A scan is running; stop it before the session can be loaded", true));
                    return;
                }
                if (!File.Exists(lSessionPath))
                {
                    pFaults.Add(Fault("SESSION_MISSING", "session", $"Session file not found: {lSessionPath}", false));
                    return;
                }
                try
                {
                    lApp.LoadSession(lSessionPath);
                    mLoadedSessionPath = lSessionPath;
                    Console.WriteLine($"[bridge] loaded session {lSessionPath}");
                }
                catch (CreaformApi.Exception ex)
                {
                    pFaults.Add(Fault("SESSION_LOAD_FAILED", "session", $"LoadSession({lSessionPath}) failed: {ex.Message}", false));
                    return;
                }
            }
            pInfo["sessionLoaded"] = true;

            var lScanAutomation = lApp.ScanAutomation;
            var lTracker = lApp.Tracker;
            bool lTrackerRequired = lScanAutomation != null && lScanAutomation.IsTrackerRequiredToScan;
            pInfo["isTrackerRequiredToScan"] = lTrackerRequired;

            // 5-6. C-Track
            if (lTracker == null)
            {
                if (lTrackerRequired)
                {
                    pFaults.Add(Fault("TRACKER_UNAVAILABLE", "hardware", "The scanner needs a tracker but no tracker module is available", false));
                }
            }
            else
            {
                TrackerOperatingStatus lStatus = lTracker.TrackerOperatingStatus;
                pInfo["trackerStatus"] = lStatus.ToString();
                // The same status text and color the Creaform.OS status window shows.
                pInfo["trackerStatusText"] = lTracker.Status;
                pInfo["trackerStatusColor"] = lTracker.StatusColor.ToString();
                pInfo["trackerIsReady"] = lTracker.IsReady;
                pInfo["trackerReferencingMode"] = lTracker.ReferencingMode.ToString();
                pInfo["lastCalibrationDate"] = lTracker.LastCalibrationDate.ToString("o");

                if (lStatus != TrackerOperatingStatus.Ready)
                {
                    pFaults.Add(TrackerFault(lStatus));
                }
                else if (lTrackerRequired && !lTracker.IsReady)
                {
                    pFaults.Add(Fault("TRACKER_NOT_READY", "hardware", "C-Track status is Ready but Tracker.IsReady is false", true));
                }
            }

            // 7-9. MetraSCAN
            if (lScan == null || lScanAutomation == null)
            {
                pFaults.Add(Fault("SCANNER_UNAVAILABLE", "hardware", "The scanning module returned no scanner handle", false));
                return;
            }

            bool lSoftwareTrigger = lScanAutomation.IsSoftwareTriggerSupported;
            bool lScannerConfig = lScanAutomation.IsScannerConfigurationAvailable();
            bool lScannerReady = lScan.IsScannerReady;
            bool lReadyToScan = lScan.IsReadyToScan;
            bool lScanningStarted = lScan.IsScanningStarted;
            pInfo["isSoftwareTriggerSupported"] = lSoftwareTrigger;
            pInfo["isScannerConfigurationAvailable"] = lScannerConfig;
            pInfo["isScannerReady"] = lScannerReady;
            pInfo["isReadyToScan"] = lReadyToScan;
            pInfo["isScanningStarted"] = lScanningStarted;

            if (!lSoftwareTrigger)
            {
                pFaults.Add(Fault("SCANNER_NO_SW_TRIGGER", "module", "The scanner does not support the software trigger", false));
            }
            if (!lScannerConfig)
            {
                pFaults.Add(Fault("SCANNER_NO_CONFIG", "module", "Scanner configuration is not available", false));
            }
            if (!lScannerReady)
            {
                pFaults.Add(Fault("SCANNER_NOT_READY", "hardware", "Scan.IsScannerReady is false", true));
            }
            if (!lReadyToScan)
            {
                pFaults.Add(Fault("SCANNER_NOT_READY_TO_SCAN", "hardware", "Scan.IsReadyToScan is false", true));
            }
            if (lScanningStarted)
            {
                pFaults.Add(Fault("SCAN_IN_PROGRESS", "session", "A scan is running; stop it before readiness can pass", true));
            }
        }

        private static Dictionary<string, object?> TrackerFault(TrackerOperatingStatus pStatus)
        {
            return pStatus switch
            {
                TrackerOperatingStatus.CalibTooOld => Fault("TRACKER_CALIB_TOO_OLD", "calibration", "C-Track calibration is too old; recalibrate the C-Track", false),
                TrackerOperatingStatus.CalibRequired => Fault("TRACKER_CALIB_REQUIRED", "calibration", "C-Track calibration is required", false),
                TrackerOperatingStatus.CalibRequiredTooHot => Fault("TRACKER_CALIB_REQUIRED_TOO_HOT", "calibration", "C-Track calibration is required: temperature is too high for the current calibration", false),
                TrackerOperatingStatus.CalibRequiredTooCold => Fault("TRACKER_CALIB_REQUIRED_TOO_COLD", "calibration", "C-Track calibration is required: temperature is too low for the current calibration", false),
                TrackerOperatingStatus.Warming => Fault("TRACKER_WARMING", "transient", "C-Track is warming up", true),
                TrackerOperatingStatus.TempOutOfRange => Fault("TRACKER_TEMP_OUT_OF_RANGE", "transient", "C-Track temperature is out of range", true),
                TrackerOperatingStatus.NotSynchronized => Fault("TRACKER_NOT_SYNCHRONIZED", "transient", "C-Track is not synchronized", true),
                TrackerOperatingStatus.Synchronizing => Fault("TRACKER_SYNCHRONIZING", "transient", "C-Track is synchronizing", true),
                TrackerOperatingStatus.NotReferenced => Fault("TRACKER_NOT_REFERENCED", "hardware", "C-Track is not referenced", false),
                TrackerOperatingStatus.Damaged => Fault("TRACKER_DAMAGED", "hardware", "C-Track reports that it is damaged", false),
                _ => Fault("TRACKER_UNKNOWN", "hardware", $"C-Track operating status is {pStatus}", false),
            };
        }

        private static Dictionary<string, object?> Fault(string pCode, string pCategory, string pMessage, bool pRetryable)
        {
            return new Dictionary<string, object?>
            {
                ["code"] = pCode,
                ["category"] = pCategory,
                ["retryable"] = pRetryable,
                ["message"] = pMessage,
            };
        }

        private static string CauseCode(CreaformApi.Exception.Cause pCause)
        {
            return pCause switch
            {
                CreaformApi.Exception.Cause.NotInstalled => "NOT_INSTALLED",
                CreaformApi.Exception.Cause.NotStarted => "NOT_STARTED",
                CreaformApi.Exception.Cause.NotConnected => "NOT_CONNECTED",
                CreaformApi.Exception.Cause.ClientVersionIncompatible => "CLIENT_VERSION_INCOMPATIBLE",
                CreaformApi.Exception.Cause.ConnectionFailed => "CONNECTION_FAILED",
                _ => "CREAFORM_ERROR",
            };
        }

        private ConnectParameters BuildConnectParameters()
        {
            ConnectOptions lOptions = mConfig.Connect;
            return new ConnectParameters
            {
                ShowSplashScreen = lOptions.ShowSplashScreen,
                ShowInTaskbar = lOptions.ShowInTaskbar,
                ShowTrayIcon = lOptions.ShowTrayIcon,
                SoftwareName = lOptions.SoftwareName,
                SilentMessage = lOptions.SilentMessage,
                ManageStatusWindowProgramatically = lOptions.ManageStatusWindowProgramatically,
            };
        }

        // Runs on a background task after Exit(). Done when the Disconnected event fires or IsConnected turns false.
        private void WaitForExit()
        {
            DateTime lDeadline = DateTime.UtcNow + ShutdownWait;
            bool lClosed = false;
            while (DateTime.UtcNow < lDeadline)
            {
                if (mApiDisconnected.Wait(TimeSpan.FromMilliseconds(200)))
                {
                    lClosed = true;
                    break;
                }
                try
                {
                    if (!ApiManager.IsConnected)
                    {
                        lClosed = true;
                        break;
                    }
                }
                catch (System.Exception)
                {
                    // Keep waiting until the deadline.
                }
            }

            Console.WriteLine(lClosed
                ? "[bridge] Creaform.OS closed"
                : $"[bridge] Creaform.OS did not report closing within {mConfig.ShutdownWaitS} s");
            mExitFinished.Set();
        }

        // Caller must hold mLock.
        private void AttachDisconnectHandler()
        {
            if (!mDisconnectHandlerAttached)
            {
                ApiManager.Disconnected += OnApiDisconnected;
                mDisconnectHandlerAttached = true;
            }
        }

        // Runs on a Creaform API thread.
        private void OnApiDisconnected()
        {
            // Signalled before taking the lock so WaitForExit sees it even while Shutdown holds the lock.
            mApiDisconnected.Set();

            lock (mLock)
            {
                mLoadedSessionPath = null;
                if (mDisconnectHandlerAttached)
                {
                    ApiManager.Disconnected -= OnApiDisconnected;
                    mDisconnectHandlerAttached = false;
                }
            }

            // Raised after leaving the lock so subscribers cannot deadlock against it.
            Disconnected?.Invoke();
        }
    }
}
