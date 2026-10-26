using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace CrCreaformApiCSharpaformOSWrapper
{
    /// <summary>
    /// Assembly resolver setup for CreaformAPI DLLs
    /// </summary>
    public static class AssemblyResolverSetup
    {
        private static bool IsAssemblyResolverSetup = false;
        private static readonly object ResolverLock = new object();

        /// <summary>
        /// Sets up the assembly resolver for CreaformAPI DLLs.
        /// This should be called explicitly by library entry points before using Creaform API types.
        /// </summary>
        public static void SetupAssemblyResolver()
        {
            // Only register the handler once to avoid duplicate registrations
            lock (ResolverLock)
            {
                if (!IsAssemblyResolverSetup)
                {
                    AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
                    IsAssemblyResolverSetup = true;
                }
            }
        }

        /// <summary>
        /// Get the installation directory of CreaformOS
        /// </summary>
        /// <returns>Installation path, or null if not installed</returns>
        public static string GetInstallationPath()
        {
            RegistryKey regKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Creaform\CreaformApi\");

            if (regKey != null)
            {
                string lCreaformOSInstallationPath = regKey.GetValue("InstallationPath", null)?.ToString();
                return lCreaformOSInstallationPath;
            }

            return null;
        }

        /// <summary>
        /// Custom assembly resolver that searches in common locations
        /// </summary>
        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                // Get the assembly name
                var lAssemblyName = new AssemblyName(args.Name);
                string lFileName = lAssemblyName.Name + ".dll";

                if (lFileName != "CreaformApi.dll" && lFileName != "CreaformAutomationApi.dll")
                {
                    return null;
                }

                string lCreaformOSInstallationPath = GetInstallationPath();

                // Search paths
                var lPathsToSearch = new List<string>
                {
                    AppDomain.CurrentDomain.BaseDirectory
                };

                if (!string.IsNullOrEmpty(lCreaformOSInstallationPath))
                {
                    lPathsToSearch.Add(lCreaformOSInstallationPath);
                }

                // Try to find and load the assembly
                foreach (string lPathToSearch in lPathsToSearch)
                {
                    if (string.IsNullOrEmpty(lPathToSearch))
                    {
                        continue;
                    }

                    string lAssemblyPath = Path.Combine(lPathToSearch, lFileName);

                    if (File.Exists(lAssemblyPath))
                    {
                        try
                        {
                            return Assembly.LoadFrom(lAssemblyPath);
                        }
                        catch (Exception ex)
                        {
                            // Log load failure
                            Debug.WriteLine($"Failed to load assembly from {lAssemblyPath}: {ex.Message}");
                        }
                    }
                }

                // Assembly not found
                Debug.WriteLine($"Assembly not resolved: {args.Name}");
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in assembly resolver: {ex.Message}");
            }

            return null;
        }
    }
}
