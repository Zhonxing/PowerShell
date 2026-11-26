// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System;
using System.IO;

namespace Microsoft.PowerShell.Adb
{
    /// <summary>
    /// Resolves adb executable path, preferring system PATH and falling back to bundled adb.
    /// </summary>
    internal static class AdbLocator
    {
        private const string AdbExeName = "adb.exe";

        /// <summary>
        /// Resolves the full path to adb.exe, checking system PATH first and then bundled locations.
        /// </summary>
        internal static string? ResolveAdbPath(out bool isBundled)
        {
            isBundled = false;

            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (string entry in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    try
                    {
                        string candidate = Path.Combine(entry.Trim('"'), AdbExeName);
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                    catch
                    {
                        // Ignore malformed PATH entries.
                    }
                }
            }

            string? exeDir = GetPwshDirectory();
            if (!string.IsNullOrEmpty(exeDir))
            {
                string bundledSubDir = Path.Combine(exeDir, "adb", AdbExeName);
                if (File.Exists(bundledSubDir))
                {
                    isBundled = true;
                    return bundledSubDir;
                }

                string bundledRoot = Path.Combine(exeDir, AdbExeName);
                if (File.Exists(bundledRoot))
                {
                    isBundled = true;
                    return bundledRoot;
                }
            }

            return null;
        }

        /// <summary>
        /// Ensures adb is available on PATH by appending the bundled adb directory when system PATH lacks adb.
        /// </summary>
        internal static void EnsureAdbInPath()
        {
            string? adbPath = ResolveAdbPath(out bool isBundled);
            if (!isBundled || string.IsNullOrEmpty(adbPath))
            {
                return;
            }

            string? adbDir = Path.GetDirectoryName(adbPath);
            if (string.IsNullOrEmpty(adbDir))
            {
                return;
            }

            string? currentPath = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(currentPath))
            {
                Environment.SetEnvironmentVariable("PATH", adbDir);
                return;
            }

            if (!currentPath.Contains(adbDir, StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("PATH", currentPath + Path.PathSeparator + adbDir);
            }
        }

        internal static string? GetPwshDirectory()
        {
            string? processPath = Environment.ProcessPath;
            return string.IsNullOrEmpty(processPath) ? null : Path.GetDirectoryName(processPath);
        }
    }
}
