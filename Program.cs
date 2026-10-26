using System.Runtime.CompilerServices;
using CrCreaformApiCSharpaformOSWrapper;

namespace CreaformBridge
{
    internal static class Program
    {
        private static async Task<int> Main(string[] pArgs)
        {
            // Must run before any Creaform type is loaded so the DLLs resolve from the install path.
            AssemblyResolverSetup.SetupAssemblyResolver();

            string lConfigPath = pArgs.Length >= 2 && pArgs[0] == "--config"
                ? pArgs[1]
                : Path.Combine(AppContext.BaseDirectory, "bridge.config.json");

            BridgeConfig lConfig;
            try
            {
                lConfig = BridgeConfig.Load(lConfigPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[bridge] could not read config {lConfigPath}: {ex.Message}");
                return 1;
            }

            return await RunBridgeAsync(lConfig);
        }

        // Separate method so the Creaform types are only touched after the resolver is registered.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static async Task<int> RunBridgeAsync(BridgeConfig pConfig)
        {
            using var lCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                lCts.Cancel();
            };

            var lSession = new CreaformSession(pConfig);
            var lServer = new TcpListenerServer(lSession, pConfig);
            lSession.Disconnected += () =>
            {
                Console.WriteLine("[bridge] Creaform.OS connection lost");
                lServer.Broadcast("disconnected");
            };

            int lExitCode = 0;
            try
            {
                await lServer.RunAsync(lCts.Token);
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                Console.Error.WriteLine($"[bridge] could not listen on {pConfig.BindAddress}:{pConfig.Port}: {ex.Message}");
                lExitCode = 1;
            }

            // Stopping the bridge also closes Creaform.OS so no CreaformAPI process is left behind.
            Result lShutdown = lSession.Shutdown();
            Console.WriteLine($"[bridge] stopping: {lShutdown.Code}: {lShutdown.Message}");
            lSession.WaitForShutdown(TimeSpan.FromSeconds(pConfig.ShutdownWaitS));

            return lExitCode;
        }
    }
}
