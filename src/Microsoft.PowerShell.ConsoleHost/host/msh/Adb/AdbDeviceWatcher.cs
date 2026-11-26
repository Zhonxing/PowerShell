// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.PowerShell.Adb
{
    /// <summary>
    /// Single-instance background watcher that tracks ADB devices via smartsocket host:track-devices
    /// and updates the Windows Terminal JSON Fragment in real time.
    /// </summary>
    internal static class AdbDeviceWatcher
    {
        internal const string WatcherMutexName = @"Local\SuperPowerShell.AdbWatch.v1";
        private const string AdbHost = "127.0.0.1";
        private const int AdbPort = 5037;
        private const string TrackDevicesCommand = "0012host:track-devices";

        /// <summary>
        /// Asynchronously ensures the single-instance watcher process is running when Windows Terminal is installed.
        /// Never blocks interactive startup.
        /// </summary>
        internal static void EnsureWatcherRunningAsync()
        {
            _ = Task.Run(() =>
            {
                try
                {
                    if (!AdbTerminalSync.IsWindowsTerminalInstalled())
                    {
                        return;
                    }

                    if (IsWatcherAlreadyRunning())
                    {
                        return;
                    }

                    string? pwshPath = Environment.ProcessPath;
                    if (string.IsNullOrEmpty(pwshPath))
                    {
                        return;
                    }

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = pwshPath,
                        Arguments = "-AdbWatch",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                    };

                    Process.Start(startInfo);
                }
                catch
                {
                    // Never interfere with normal PowerShell startup.
                }
            });
        }

        internal static bool IsWatcherAlreadyRunning()
        {
            if (Mutex.TryOpenExisting(WatcherMutexName, out Mutex? existing))
            {
                existing.Dispose();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Entry point for `pwsh.exe -AdbWatch`. Runs until terminated.
        /// </summary>
        internal static int RunWatchLoop()
        {
            using var mutex = new Mutex(initiallyOwned: true, name: WatcherMutexName, out bool createdNew);
            if (!createdNew)
            {
                return ConsoleHost.ExitCodeSuccess;
            }

            AdbLocator.EnsureAdbInPath();

            // Write initial base fragment immediately so SuperPowerShell appears in WT even before adb connects.
            TryWriteFragment(Array.Empty<string>());

            bool triedStartServer = false;

            while (true)
            {
                try
                {
                    using var client = new TcpClient();
                    client.Connect(AdbHost, AdbPort);
                    triedStartServer = false;

                    using NetworkStream stream = client.GetStream();
                    byte[] cmdBytes = Encoding.ASCII.GetBytes(TrackDevicesCommand);
                    stream.Write(cmdBytes, 0, cmdBytes.Length);
                    stream.Flush();

                    string status = ReadExactAscii(stream, 4);
                    if (!string.Equals(status, "OKAY", StringComparison.Ordinal))
                    {
                        Thread.Sleep(3000);
                        continue;
                    }

                    while (true)
                    {
                        string lenHex = ReadExactAscii(stream, 4);
                        if (!int.TryParse(lenHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int payloadLen))
                        {
                            break;
                        }

                        string payload = payloadLen > 0 ? ReadExactUtf8(stream, payloadLen) : string.Empty;
                        List<string> devices = ParseOnlineDevices(payload);
                        TryWriteFragment(devices);
                    }
                }
                catch
                {
                    if (!triedStartServer)
                    {
                        triedStartServer = true;
                        TryStartAdbServer();
                        Thread.Sleep(1000);
                    }
                    else
                    {
                        Thread.Sleep(3000);
                    }
                }
            }
        }

        internal static List<string> ParseOnlineDevices(string payload)
        {
            var serials = new List<string>();
            if (string.IsNullOrWhiteSpace(payload))
            {
                return serials;
            }

            using var reader = new StringReader(payload);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                string[] parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2 && string.Equals(parts[1], "device", StringComparison.OrdinalIgnoreCase))
                {
                    serials.Add(parts[0]);
                }
            }

            return serials;
        }

        private static void TryWriteFragment(IReadOnlyList<string> devices)
        {
            try
            {
                AdbTerminalSync.WriteFragment(devices);
            }
            catch
            {
                // Ignore transient filesystem errors.
            }
        }

        private static void TryStartAdbServer()
        {
            try
            {
                string? adbPath = AdbLocator.ResolveAdbPath(out _);
                if (string.IsNullOrEmpty(adbPath))
                {
                    return;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = adbPath,
                    Arguments = "start-server",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };

                using Process? proc = Process.Start(startInfo);
                proc?.WaitForExit(5000);
            }
            catch
            {
                // Ignore if adb cannot be launched.
            }
        }

        private static string ReadExactAscii(NetworkStream stream, int count)
        {
            byte[] buffer = ReadExactBytes(stream, count);
            return Encoding.ASCII.GetString(buffer);
        }

        private static string ReadExactUtf8(NetworkStream stream, int count)
        {
            byte[] buffer = ReadExactBytes(stream, count);
            return Encoding.UTF8.GetString(buffer);
        }

        private static byte[] ReadExactBytes(NetworkStream stream, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0)
                {
                    throw new EndOfStreamException("ADB server closed track-devices stream.");
                }

                offset += read;
            }

            return buffer;
        }
    }
}
