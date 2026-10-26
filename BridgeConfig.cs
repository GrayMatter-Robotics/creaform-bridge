using System.Text.Json;

namespace CreaformBridge
{
    /// <summary>Options passed to ApiManager.Connect. Defaults match the SDK sample form's checkboxes.</summary>
    public sealed class ConnectOptions
    {
        public bool ShowSplashScreen { get; set; } = true;
        public bool ShowTrayIcon { get; set; } = true;
        public bool ShowInTaskbar { get; set; } = true;
        public bool SilentMessage { get; set; } = false;
        public bool ManageStatusWindowProgramatically { get; set; } = false;
        public string SoftwareName { get; set; } = "Creaform.OS";
    }

    /// <summary>Bridge settings loaded from bridge.config.json.</summary>
    public sealed class BridgeConfig
    {
        public string BindAddress { get; set; } = "192.168.11.100";
        public int Port { get; set; } = 5555;
        public ConnectOptions Connect { get; set; } = new ConnectOptions();

        /// <summary>ExecutingApiVersion the bridge was tested with. 0 means not pinned yet, which readiness reports as a fault.</summary>
        public int ExpectedExecutingApiVersion { get; set; } = 0;

        /// <summary>Persistent session loaded by readiness. Environment variables are expanded. Empty means Documents\Adaptive_Sanding\adaptive_sanding.csf.</summary>
        public string SessionPath { get; set; } = "";

        /// <summary>How long to wait for Creaform.OS to close after Exit().</summary>
        public double ShutdownWaitS { get; set; } = 30.0;

        /// <summary>A controlling client that sends nothing for this long is treated as lost. 0 turns the check off.</summary>
        public double ClientIdleS { get; set; } = 6.0;

        /// <summary>After the controlling client is lost, close Creaform.OS if no client sends connect within this time.</summary>
        public double ClientLossShutdownS { get; set; } = 10.0;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };

        public static BridgeConfig Load(string path)
        {
            string lText = File.ReadAllText(path);
            BridgeConfig? lConfig = JsonSerializer.Deserialize<BridgeConfig>(lText, JsonOptions);
            return lConfig ?? new BridgeConfig();
        }

        public string ResolveSessionPath()
        {
            if (string.IsNullOrWhiteSpace(SessionPath))
            {
                string lDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                return Path.Combine(lDocuments, "Adaptive_Sanding", "adaptive_sanding.csf");
            }
            return Environment.ExpandEnvironmentVariables(SessionPath);
        }
    }
}
