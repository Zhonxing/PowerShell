// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.PowerShell.Adb
{
    /// <summary>
    /// Manages single-instance launch and host invocation of the WPF Command Panel sidecar.
    /// </summary>
    internal static class CommandPanelLauncher
    {
        internal const string PanelMutexName = @"Local\SuperPowerShell.CommandPanel.v1";
        internal const string ShowEventName = @"Local\SuperPowerShell.CommandPanel.Show.v1";
        internal const string CloseEventName = @"Local\SuperPowerShell.CommandPanel.Close.v1";

        private const string WindowsTerminalClass = "CASCADIA_HOSTING_WINDOW_CLASS";
        private const string ConsoleWindowClass = "ConsoleWindowClass";

        /// <summary>
        /// Asynchronously ensures the single-instance Command Panel is running for interactive sessions.
        /// Never blocks startup.
        /// </summary>
        internal static void EnsurePanelRunningAsync(bool forceShow = false)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    nint targetHwnd = FindCurrentTerminalWindow();
                    if (targetHwnd == IntPtr.Zero)
                    {
                        return;
                    }

                    if (!forceShow && !ShouldAutoLaunch(targetHwnd))
                    {
                        return;
                    }

                    if (IsPanelAlreadyRunning())
                    {
                        if (forceShow)
                        {
                            TrySignalEvent(ShowEventName);
                        }

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
                        Arguments = $"-CommandPanel -CommandPanelTarget {targetHwnd.ToInt64()}",
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

        /// <summary>
        /// Toggles the single-instance Command Panel: closes it if already open, or launches it detached otherwise.
        /// Invoked by `pwsh.exe -CommandPanelToggle` from the Windows Terminal dropdown menu.
        /// </summary>
        internal static int TogglePanelProcess()
        {
            try
            {
                if (IsPanelAlreadyRunning())
                {
                    TrySignalEvent(CloseEventName);
                    return ConsoleHost.ExitCodeSuccess;
                }

                string? pwshPath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(pwshPath))
                {
                    return ConsoleHost.ExitCodeSuccess;
                }

                nint targetHwnd = FindCurrentTerminalWindow();
                string args = targetHwnd != IntPtr.Zero
                    ? $"-CommandPanel -CommandPanelTarget {targetHwnd.ToInt64()}"
                    : "-CommandPanel";

                // UseShellExecute = true detaches the panel from the temporary WT tab's ConPTY job object
                // so the panel stays alive after the trigger tab auto-closes.
                var startInfo = new ProcessStartInfo
                {
                    FileName = pwshPath,
                    Arguments = args,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };

                Process.Start(startInfo);
            }
            catch
            {
                // Ignore launch failures so the temporary trigger tab still exits cleanly.
            }

            return ConsoleHost.ExitCodeSuccess;
        }

        /// <summary>
        /// Entry point for `pwsh.exe -CommandPanel`. Runs the WPF sidebar on a dedicated STA thread.
        /// </summary>
        internal static int RunPanelProcess(long targetHwnd)
        {
            int exitCode = ConsoleHost.ExitCodeSuccess;
            Exception? capturedException = null;

            var staThread = new Thread(() =>
            {
                try
                {
                    Assembly graphicalHost = Assembly.Load("Microsoft.PowerShell.GraphicalHost");
                    Type? appType = graphicalHost.GetType("Microsoft.Management.UI.Internal.CommandPanel.CommandPanelApp");
                    MethodInfo? runMethod = appType?.GetMethod("Run", BindingFlags.Public | BindingFlags.Static, new[] { typeof(long) });
                    if (runMethod is null)
                    {
                        exitCode = ConsoleHost.ExitCodeInitFailure;
                        return;
                    }

                    object? result = runMethod.Invoke(null, new object[] { targetHwnd });
                    if (result is int code)
                    {
                        exitCode = code;
                    }
                }
                catch (Exception ex)
                {
                    capturedException = ex;
                    exitCode = ConsoleHost.ExitCodeInitFailure;
                }
            })
            {
                Name = "CommandPanel STA Thread",
                IsBackground = false,
            };

            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join();

            _ = capturedException;
            return exitCode;
        }

        internal static bool IsPanelAlreadyRunning()
        {
            if (Mutex.TryOpenExisting(PanelMutexName, out Mutex? existing))
            {
                existing.Dispose();
                return true;
            }

            return false;
        }

        internal static void TrySignalEvent(string eventName)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(eventName, out EventWaitHandle? waitHandle))
                {
                    using (waitHandle)
                    {
                        waitHandle.Set();
                    }
                }
            }
            catch
            {
                // Ignore race conditions.
            }
        }

        private static bool ShouldAutoLaunch(nint targetHwnd)
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (!string.IsNullOrEmpty(appData))
                {
                    string configPath = Path.Combine(appData, "SuperPowerShell", "commands.json");
                    if (File.Exists(configPath))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                        if (doc.RootElement.TryGetProperty("autoLaunch", out JsonElement autoProp) &&
                            autoProp.ValueKind == JsonValueKind.False)
                        {
                            return false;
                        }
                    }
                }

                GetWindowThreadProcessId(targetHwnd, out uint pid);
                if (pid != 0)
                {
                    string dismissMarker = Path.Combine(Path.GetTempPath(), $"SuperPowerShell.PanelDismissed.{pid}");
                    if (File.Exists(dismissMarker))
                    {
                        return false;
                    }
                }
            }
            catch
            {
                // Default to auto-launch if config cannot be parsed.
            }

            return true;
        }

        private static nint FindCurrentTerminalWindow()
        {
            nint fg = GetForegroundWindow();
            if (fg != IntPtr.Zero && IsTerminalWindow(fg))
            {
                return fg;
            }

            nint consoleHwnd = GetConsoleWindow();
            if (consoleHwnd != IntPtr.Zero && IsWindowVisible(consoleHwnd))
            {
                return consoleHwnd;
            }

            nint wtHwnd = FindWindow(WindowsTerminalClass, null);
            if (wtHwnd != IntPtr.Zero && IsWindowVisible(wtHwnd))
            {
                return wtHwnd;
            }

            return consoleHwnd;
        }

        private static bool IsTerminalWindow(nint hwnd)
        {
            var sb = new StringBuilder(128);
            int len = GetClassName(hwnd, sb, sb.Capacity);
            if (len <= 0)
            {
                return false;
            }

            string cls = sb.ToString();
            return string.Equals(cls, WindowsTerminalClass, StringComparison.Ordinal) ||
                   string.Equals(cls, ConsoleWindowClass, StringComparison.Ordinal);
        }

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        [DllImport("kernel32.dll")]
        private static extern nint GetConsoleWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(nint hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern nint FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
    }
}
