// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Threading;
using System.Windows;

namespace Microsoft.Management.UI.Internal.CommandPanel
{
    /// <summary>
    /// Single-instance WPF application entry point invoked via reflection by `pwsh.exe -CommandPanel`.
    /// </summary>
    public static class CommandPanelApp
    {
        private const string PanelMutexName = @"Local\SuperPowerShell.CommandPanel.v1";
        private const string ShowEventName = @"Local\SuperPowerShell.CommandPanel.Show.v1";
        private const string CloseEventName = @"Local\SuperPowerShell.CommandPanel.Close.v1";

        /// <summary>
        /// Starts the single-instance WPF Command Panel docked to the specified terminal window handle.
        /// </summary>
        /// <param name="targetHwnd">Optional Win32 handle of the host terminal window.</param>
        /// <returns>Process exit code.</returns>
        public static int Run(long targetHwnd)
        {
            using var mutex = new Mutex(initiallyOwned: true, name: PanelMutexName, out bool createdNew);
            if (!createdNew)
            {
                try
                {
                    if (EventWaitHandle.TryOpenExisting(ShowEventName, out EventWaitHandle? existingShow))
                    {
                        using (existingShow)
                        {
                            existingShow.Set();
                        }
                    }
                }
                catch
                {
                    // Ignore race conditions.
                }

                return 0;
            }

            using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            using var closeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, CloseEventName);
            using var exitSignal = new ManualResetEventSlim(false);

            var app = Application.Current ?? new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;

            var window = new CommandPanelWindow(new IntPtr(targetHwnd));
            app.MainWindow = window;

            var listenerThread = new Thread(() =>
            {
                WaitHandle[] handles = { showEvent, closeEvent, exitSignal.WaitHandle };
                while (true)
                {
                    int idx = WaitHandle.WaitAny(handles);
                    if (idx == 0)
                    {
                        try
                        {
                            window.BringToFrontAndRedock();
                        }
                        catch
                        {
                            // Ignore if closing.
                        }
                    }
                    else if (idx == 1)
                    {
                        try
                        {
                            window.CloseSilently();
                        }
                        catch
                        {
                            // Ignore if already closed.
                        }

                        break;
                    }
                    else
                    {
                        break;
                    }
                }
            })
            {
                Name = "CommandPanel IPC Listener",
                IsBackground = true,
            };

            listenerThread.Start();

            try
            {
                app.Run(window);
            }
            finally
            {
                exitSignal.Set();
            }

            return 0;
        }
    }
}
