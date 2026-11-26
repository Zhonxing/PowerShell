// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.Management.UI.Internal.CommandPanel
{
    internal sealed class CommandItem
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("command")]
        public string Command { get; set; } = string.Empty;

        [JsonPropertyName("category")]
        public string Category { get; set; } = "自定义";
    }

    internal sealed class CommandConfig
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("autoLaunch")]
        public bool AutoLaunch { get; set; } = true;

        [JsonPropertyName("panelWidth")]
        public double PanelWidth { get; set; } = 310;

        [JsonPropertyName("commands")]
        public List<CommandItem> Commands { get; set; } = new List<CommandItem>();
    }

    internal static class CommandStore
    {
        private static readonly JsonSerializerOptions s_jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = true,
        };

        internal static string GetConfigDirectory()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "SuperPowerShell");
        }

        internal static string GetConfigPath()
        {
            return Path.Combine(GetConfigDirectory(), "commands.json");
        }

        internal static CommandConfig LoadOrCreate()
        {
            string path = GetConfigPath();
            try
            {
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    CommandConfig? parsed = JsonSerializer.Deserialize<CommandConfig>(json, s_jsonOptions);
                    if (parsed is not null)
                    {
                        parsed.Commands ??= new List<CommandItem>();
                        if (parsed.PanelWidth < 240 || parsed.PanelWidth > 600)
                        {
                            parsed.PanelWidth = 310;
                        }

                        return parsed;
                    }
                }
            }
            catch
            {
                // Fall through to default presets if file is corrupted.
            }

            CommandConfig defaults = CreateDefaultConfig();
            Save(defaults);
            return defaults;
        }

        internal static void Save(CommandConfig config)
        {
            string dir = GetConfigDirectory();
            Directory.CreateDirectory(dir);

            string path = GetConfigPath();
            string tmpPath = path + ".tmp";
            string json = JsonSerializer.Serialize(config, s_jsonOptions);

            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, path, overwrite: true);
        }

        private static CommandConfig CreateDefaultConfig()
        {
            return new CommandConfig
            {
                Version = 1,
                AutoLaunch = true,
                PanelWidth = 310,
                Commands = new List<CommandItem>
                {
                    new CommandItem { Name = "查看在线设备", Command = "adb devices -l", Category = "ADB 常用" },
                },
            };
        }
    }
}
