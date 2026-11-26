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

            var activeProfiles = new List<(string Guid, string Name)>(deviceSerials.Count + 1);
            string baseGuid = FormatGuid(CreateDeterministicGuid("base:SuperPowerShell"));
            activeProfiles.Add((baseGuid, "SuperPowerShell"));

            var profiles = new List<Dictionary<string, object>>(deviceSerials.Count + 1)
            {
                new Dictionary<string, object>
                {
                    ["guid"] = baseGuid,
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

                string devGuid = FormatGuid(CreateDeterministicGuid("device:" + serial));
                activeProfiles.Add((devGuid, serial));

                profiles.Add(new Dictionary<string, object>
                {
                    ["guid"] = devGuid,
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

            bool fragmentChanged = true;
            if (File.Exists(fragmentPath))
            {
                try
                {
                    string existing = File.ReadAllText(fragmentPath, Encoding.UTF8);
                    if (string.Equals(existing, json, StringComparison.Ordinal))
                    {
                        fragmentChanged = false;
                    }
                }
                catch
                {
                    // Proceed with overwrite if read fails.
                }
            }

            if (fragmentChanged)
            {
                string tempPath = fragmentPath + ".tmp";
                File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(tempPath, fragmentPath, overwrite: true);
            }

            SyncWindowsTerminalSettings(activeProfiles, forceTouch: fragmentChanged);
        }

        /// <summary>
        /// Synchronizes active SuperPowerShell profile stubs in Windows Terminal's settings.json
        /// so WT immediately hot-reloads new/reconnected devices even when WT holds generatedProfiles in memory,
        /// and removes disconnected SuperPowerShell device stubs to prevent settings.json bloat.
        /// </summary>
        private static void SyncWindowsTerminalSettings(List<(string Guid, string Name)> activeProfiles, bool forceTouch)
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
                        SyncSingleSettingsFile(Path.Combine(dir, "LocalState", "settings.json"), activeProfiles, forceTouch);
                    }
                }

                string unpackagedSettings = Path.Combine(localAppData, "Microsoft", "Windows Terminal", "settings.json");
                SyncSingleSettingsFile(unpackagedSettings, activeProfiles, forceTouch);
            }
            catch
            {
                // Best-effort live reload notification.
            }
        }

        private static void SyncSingleSettingsFile(string settingsPath, List<(string Guid, string Name)> activeProfiles, bool forceTouch)
        {
            if (!File.Exists(settingsPath))
            {
                return;
            }

            bool modified = false;
            try
            {
                string settingsText = File.ReadAllText(settingsPath, Encoding.UTF8);
                var docOptions = new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                };
                var root = System.Text.Json.Nodes.JsonNode.Parse(settingsText, documentOptions: docOptions)?.AsObject();
                if (root is not null &&
                    root["profiles"] is System.Text.Json.Nodes.JsonObject profilesObj &&
                    profilesObj["list"] is System.Text.Json.Nodes.JsonArray listArr)
                {
                    var activeGuidSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach ((string guid, _) in activeProfiles)
                    {
                        activeGuidSet.Add(guid);
                    }

                    var existingGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    // 1. Remove disconnected SuperPowerShell stubs; record existing active ones.
                    for (int i = listArr.Count - 1; i >= 0; i--)
                    {
                        if (listArr[i] is not System.Text.Json.Nodes.JsonObject item)
                        {
                            continue;
                        }

                        string? source = item["source"]?.GetValue<string>();
                        string? guid = item["guid"]?.GetValue<string>();

                        if (!string.IsNullOrEmpty(guid))
                        {
                            existingGuids.Add(guid);
                        }

                        if (string.Equals(source, FragmentAppFolder, StringComparison.OrdinalIgnoreCase))
                        {
                            if (string.IsNullOrEmpty(guid) || !activeGuidSet.Contains(guid))
                            {
                                listArr.RemoveAt(i);
                                modified = true;
                            }
                            else if (item["hidden"]?.GetValue<bool>() == true)
                            {
                                item["hidden"] = false;
                                modified = true;
                            }
                        }
                    }

                    // 2. Append any active SuperPowerShell device missing from settings.json.
                    foreach ((string guid, string name) in activeProfiles)
                    {
                        if (!existingGuids.Contains(guid))
                        {
                            var stub = new System.Text.Json.Nodes.JsonObject
                            {
                                ["guid"] = guid,
                                ["hidden"] = false,
                                ["name"] = name,
                                ["source"] = FragmentAppFolder,
                            };
                            listArr.Add(stub);
                            modified = true;
                        }
                    }

                    if (modified)
                    {
                        string tmpPath = settingsPath + ".tmp";
                        File.WriteAllText(tmpPath, root.ToJsonString(s_jsonOptions), new UTF8Encoding(false));
                        File.Move(tmpPath, settingsPath, overwrite: true);
                        return;
                    }
                }
            }
            catch
            {
                // Fall back to timestamp touch if settings.json cannot be parsed.
            }

            if (forceTouch)
            {
                File.SetLastWriteTimeUtc(settingsPath, DateTime.UtcNow);
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
