// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Microsoft.PowerShell.Adb
{
    /// <summary>
    /// Syncs connected ADB devices into Windows Terminal JSON Fragments for dynamic new-tab dropdown entries.
    /// </summary>
    internal static class AdbTerminalSync
    {
        private const string FragmentAppFolder = "SuperPowerShell";
        private const string FragmentFileName = "adb-devices.json";

        // Fixed namespace UUID for deterministic UUIDv5 generation.
        private static readonly Guid s_namespaceGuid = new("8d4b7c2f-5c9e-4c61-a6e2-6b90f3d7a101");

        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>
        /// Returns true if Windows Terminal appears to be installed on this machine.
        /// </summary>
        internal static bool IsWindowsTerminalInstalled()
        {
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(localAppData))
                {
                    string packagesDir = Path.Combine(localAppData, "Packages");
                    if (Directory.Exists(packagesDir))
                    {
                        foreach (string dir in Directory.EnumerateDirectories(packagesDir, "Microsoft.WindowsTerminal*"))
                        {
                            if (!string.IsNullOrEmpty(dir))
                            {
                                return true;
                            }
                        }
                    }

                    string wtDataDir = Path.Combine(localAppData, "Microsoft", "Windows Terminal");
                    if (Directory.Exists(wtDataDir))
                    {
                        return true;
                    }
                }

                string? pathEnv = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrEmpty(pathEnv))
                {
                    foreach (string entry in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (File.Exists(Path.Combine(entry.Trim('"'), "wt.exe")))
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // Ignore filesystem probe failures.
            }

            return false;
        }

        /// <summary>
        /// Gets the Windows Terminal Fragment file path for SuperPowerShell ADB devices.
        /// </summary>
        internal static string GetFragmentFilePath()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Microsoft", "Windows Terminal", "Fragments", FragmentAppFolder, FragmentFileName);
        }

        /// <summary>
        /// Writes the Windows Terminal JSON fragment atomically when its content has changed.
        /// </summary>
        internal static void WriteFragment(IReadOnlyList<string> deviceSerials)
        {
            string? pwshPath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(pwshPath))
            {
                return;
            }

            var profiles = new List<Dictionary<string, object>>(deviceSerials.Count + 1)
            {
                new Dictionary<string, object>
                {
                    ["guid"] = FormatGuid(CreateDeterministicGuid("base:SuperPowerShell")),
                    ["name"] = "SuperPowerShell",
                    ["commandline"] = $"\"{pwshPath}\" -NoLogo",
                },
            };

            foreach (string serial in deviceSerials)
            {
                if (string.IsNullOrWhiteSpace(serial))
                {
                    continue;
                }

                profiles.Add(new Dictionary<string, object>
                {
                    ["guid"] = FormatGuid(CreateDeterministicGuid("device:" + serial)),
                    ["name"] = serial,
                    ["tabTitle"] = serial,
                    ["suppressApplicationTitle"] = true,
                    ["commandline"] = $"\"{pwshPath}\" -NoLogo -AdbDevice \"{serial}\"",
                });
            }

            var payload = new Dictionary<string, object>
            {
                ["profiles"] = profiles,
            };

            string json = JsonSerializer.Serialize(payload, s_jsonOptions);
            string fragmentPath = GetFragmentFilePath();
            string? dir = Path.GetDirectoryName(fragmentPath);
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            Directory.CreateDirectory(dir);

            if (File.Exists(fragmentPath))
            {
                try
                {
                    string existing = File.ReadAllText(fragmentPath, Encoding.UTF8);
                    if (string.Equals(existing, json, StringComparison.Ordinal))
                    {
                        return;
                    }
                }
                catch
                {
                    // Proceed with overwrite if read fails.
                }
            }

            string tempPath = fragmentPath + ".tmp";
            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, fragmentPath, overwrite: true);

            NotifyWindowsTerminalReload();
        }

        /// <summary>
        /// Touches Windows Terminal settings.json timestamp (without modifying contents) so WT's
        /// FileSystemWatcher immediately hot-reloads updated Fragments into the + dropdown menu.
        /// </summary>
        private static void NotifyWindowsTerminalReload()
        {
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(localAppData))
                {
                    return;
                }

                string packagesDir = Path.Combine(localAppData, "Packages");
                if (Directory.Exists(packagesDir))
                {
                    foreach (string dir in Directory.EnumerateDirectories(packagesDir, "Microsoft.WindowsTerminal*"))
                    {
                        string settingsPath = Path.Combine(dir, "LocalState", "settings.json");
                        if (File.Exists(settingsPath))
                        {
                            File.SetLastWriteTimeUtc(settingsPath, DateTime.UtcNow);
                        }
                    }
                }

                string unpackagedSettings = Path.Combine(localAppData, "Microsoft", "Windows Terminal", "settings.json");
                if (File.Exists(unpackagedSettings))
                {
                    File.SetLastWriteTimeUtc(unpackagedSettings, DateTime.UtcNow);
                }
            }
            catch
            {
                // Best-effort live reload notification.
            }
        }

        private static string FormatGuid(Guid guid)
            => "{" + guid.ToString("D") + "}";

        /// <summary>
        /// Generates an RFC 4122 version 5 (SHA-1) deterministic GUID for a given name.
        /// </summary>
        private static Guid CreateDeterministicGuid(string name)
        {
            byte[] namespaceBytes = s_namespaceGuid.ToByteArray();
            SwapGuidByteOrder(namespaceBytes);

            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            byte[] combined = new byte[namespaceBytes.Length + nameBytes.Length];
            Buffer.BlockCopy(namespaceBytes, 0, combined, 0, namespaceBytes.Length);
            Buffer.BlockCopy(nameBytes, 0, combined, namespaceBytes.Length, nameBytes.Length);

            byte[] hash = SHA1.HashData(combined);
            byte[] newGuid = new byte[16];
            Array.Copy(hash, 0, newGuid, 0, 16);

            // Set version to 5 and variant to RFC 4122.
            newGuid[6] = (byte)((newGuid[6] & 0x0F) | 0x50);
            newGuid[8] = (byte)((newGuid[8] & 0x3F) | 0x80);

            SwapGuidByteOrder(newGuid);
            return new Guid(newGuid);
        }

        private static void SwapGuidByteOrder(byte[] guid)
        {
            (guid[0], guid[3]) = (guid[3], guid[0]);
            (guid[1], guid[2]) = (guid[2], guid[1]);
            (guid[4], guid[5]) = (guid[5], guid[4]);
            (guid[6], guid[7]) = (guid[7], guid[6]);
        }
    }
}
