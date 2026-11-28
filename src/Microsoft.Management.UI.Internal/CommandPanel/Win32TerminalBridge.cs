// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Management.UI.Internal.CommandPanel
{
    internal readonly struct NativeRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;

        public NativeRect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    internal static class Win32TerminalBridge
    {
        internal const string WindowsTerminalClass = "CASCADIA_HOSTING_WINDOW_CLASS";
        internal const string ConsoleWindowClass = "ConsoleWindowClass";

        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const int DWMWA_CLOAKED = 14;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWCP_ROUND = 2;
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOPMOST = 0x00000008L;
        private const uint GW_HWNDPREV = 3;
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const ushort VK_RETURN = 0x0D;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private static readonly nint HWND_BOTTOM = new(1);
        private static readonly nint HWND_TOPMOST = new(-1);
        private static readonly nint HWND_NOTOPMOST = new(-2);

        internal static nint FindTerminalWindow(nint preferredHwnd)
        {
            if (preferredHwnd != IntPtr.Zero && IsWindow(preferredHwnd) && IsTerminalWindow(preferredHwnd))
            {
                return preferredHwnd;
            }

            nint fg = GetForegroundWindow();
            if (fg != IntPtr.Zero && IsTerminalWindow(fg))
            {
                return fg;
            }

            nint wt = FindFirstVisibleByClass(WindowsTerminalClass);
            if (wt != IntPtr.Zero)
            {
                return wt;
            }

            nint con = FindFirstVisibleByClass(ConsoleWindowClass);
            if (con != IntPtr.Zero)
            {
                return con;
            }

            return IntPtr.Zero;
        }

        private static nint FindFirstVisibleByClass(string targetClass)
        {
            nint found = IntPtr.Zero;
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd))
                {
                    return true;
                }

                var sb = new StringBuilder(128);
                int len = GetClassName(hwnd, sb, sb.Capacity);
                if (len > 0 && string.Equals(sb.ToString(), targetClass, StringComparison.Ordinal))
                {
                    found = hwnd;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            return found;
        }

        internal static bool IsValidWindow(nint hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

        internal static bool IsMinimized(nint hwnd) => hwnd != IntPtr.Zero && IsIconic(hwnd);

        internal static bool IsTargetHiddenOrCloaked(nint hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            {
                return true;
            }

            if (IsIconic(hwnd) || !IsWindowVisible(hwnd))
            {
                return true;
            }

            return IsWindowCloaked(hwnd);
        }

        internal static bool IsWindowTopmost(nint hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            nint exStyle = GetWindowLongPtrCompat(hwnd, GWL_EXSTYLE);
            return (exStyle.ToInt64() & WS_EX_TOPMOST) != 0;
        }

        private static bool IsWindowCloaked(nint hwnd)
        {
            try
            {
                return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
            }
            catch
            {
                return false;
            }
        }

        internal static nint GetCurrentForegroundWindow() => GetForegroundWindow();

        internal static bool IsTerminalWindow(nint hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

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

        internal static uint GetWindowProcessId(nint hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return 0;
            }

            GetWindowThreadProcessId(hwnd, out uint pid);
            return pid;
        }

        internal static string GetWindowTitleText(nint hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return string.Empty;
            }

            var sb = new StringBuilder(256);
            int len = GetWindowText(hwnd, sb, sb.Capacity);
            return len > 0 ? sb.ToString() : string.Empty;
        }

        internal static bool TryGetWindowVisualRect(nint hwnd, out NativeRect rect)
        {
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT dwmRect, Marshal.SizeOf<RECT>()) == 0 &&
                dwmRect.Right > dwmRect.Left &&
                dwmRect.Bottom > dwmRect.Top)
            {
                rect = new NativeRect(dwmRect.Left, dwmRect.Top, dwmRect.Right, dwmRect.Bottom);
                return true;
            }

            if (GetWindowRect(hwnd, out RECT winRect))
            {
                rect = new NativeRect(winRect.Left, winRect.Top, winRect.Right, winRect.Bottom);
                return true;
            }

            rect = default;
            return false;
        }

        internal static bool TryGetMonitorWorkArea(nint hwnd, out NativeRect workArea)
        {
            nint hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (hMonitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(hMonitor, ref info))
                {
                    workArea = new NativeRect(
                        info.rcWork.Left,
                        info.rcWork.Top,
                        info.rcWork.Right,
                        info.rcWork.Bottom);
                    return true;
                }
            }

            workArea = default;
            return false;
        }

        internal static void ApplyRoundedWindowCorners(nint hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            try
            {
                int darkMode = 1;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

                int preference = DWMWCP_ROUND;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));

                // COLORREF is 0x00BBGGRR (#383840 -> 0x00403838)
                int borderColor = 0x00403838;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
            }
            catch
            {
                // Ignored on Windows 10 where these attributes are unsupported.
            }
        }

        /// <summary>
        /// Promotes the panel above the host terminal (via TOPMOST without stealing focus, since Win32 blocks
        /// non-foreground processes from raising a normal window above another process's foreground window)
        /// when the terminal or panel is active, and reliably demotes it below covering windows otherwise.
        /// </summary>
        internal static void SyncPanelZOrder(nint panelHwnd, nint targetHwnd, nint foregroundHwnd, bool isRelatedActive)
        {
            if (panelHwnd == IntPtr.Zero || !IsWindow(panelHwnd))
            {
                return;
            }

            bool panelTopmost = IsWindowTopmost(panelHwnd);

            if (isRelatedActive)
            {
                if (!panelTopmost || (targetHwnd != IntPtr.Zero && !IsWindowAbove(panelHwnd, targetHwnd)))
                {
                    SetWindowPos(panelHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }

                return;
            }

            if (panelTopmost)
            {
                SetWindowPos(panelHwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }

            nint insertAfter = FindVisibleNonTopmostWindowAbove(targetHwnd, panelHwnd);
            if (insertAfter == IntPtr.Zero &&
                foregroundHwnd != IntPtr.Zero &&
                foregroundHwnd != panelHwnd &&
                foregroundHwnd != targetHwnd &&
                IsWindow(foregroundHwnd) &&
                IsWindowVisible(foregroundHwnd) &&
                !IsWindowTopmost(foregroundHwnd))
            {
                insertAfter = foregroundHwnd;
            }

            if (insertAfter != IntPtr.Zero && IsWindowAbove(panelHwnd, insertAfter))
            {
                bool moved = SetWindowPos(panelHwnd, insertAfter, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                if (!moved || IsWindowAbove(panelHwnd, insertAfter))
                {
                    // Fallback when UIPI prevents positioning relative to an elevated foreground window.
                    SetWindowPos(panelHwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
            }
        }

        private static bool IsWindowAbove(nint upperHwnd, nint lowerHwnd)
        {
            if (upperHwnd == IntPtr.Zero || lowerHwnd == IntPtr.Zero || upperHwnd == lowerHwnd)
            {
                return false;
            }

            nint cur = GetWindow(lowerHwnd, GW_HWNDPREV);
            int guard = 0;
            while (cur != IntPtr.Zero && guard++ < 256)
            {
                if (cur == upperHwnd)
                {
                    return true;
                }

                cur = GetWindow(cur, GW_HWNDPREV);
            }

            return false;
        }

        private static nint FindVisibleNonTopmostWindowAbove(nint targetHwnd, nint panelHwnd)
        {
            if (targetHwnd == IntPtr.Zero || !IsWindow(targetHwnd))
            {
                return IntPtr.Zero;
            }

            nint cur = GetWindow(targetHwnd, GW_HWNDPREV);
            int guard = 0;
            while (cur != IntPtr.Zero && guard++ < 256)
            {
                if (cur != panelHwnd &&
                    IsWindowVisible(cur) &&
                    !IsWindowTopmost(cur) &&
                    !IsWindowCloaked(cur))
                {
                    return cur;
                }

                cur = GetWindow(cur, GW_HWNDPREV);
            }

            return IntPtr.Zero;
        }

        private static nint GetWindowLongPtrCompat(nint hWnd, int nIndex)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(hWnd, nIndex)
                : new nint(GetWindowLong32(hWnd, nIndex));
        }

        private const int InterCommandDelayMs = 150;

        internal static async Task<bool> InjectCommandAsync(nint targetHwnd, string command, bool execute)
        {
            List<string> lines = CommandStore.ParseCommandLines(command);
            if (targetHwnd == IntPtr.Zero || !IsWindow(targetHwnd) || lines.Count == 0)
            {
                return false;
            }

            if (IsIconic(targetHwnd))
            {
                ShowWindow(targetHwnd, 9); // SW_RESTORE
            }

            SetForegroundWindow(targetHwnd);
            await Task.Delay(35).ConfigureAwait(false);

            if (!execute)
            {
                string joined = lines.Count == 1 ? lines[0] : string.Join("; ", lines);
                return SendTextLine(joined, pressEnter: false);
            }

            for (int i = 0; i < lines.Count; i++)
            {
                if (!IsWindow(targetHwnd))
                {
                    return false;
                }

                if (!SendTextLine(lines[i], pressEnter: true))
                {
                    return false;
                }

                if (i < lines.Count - 1)
                {
                    await Task.Delay(InterCommandDelayMs).ConfigureAwait(false);
                }
            }

            return true;
        }

        private static bool SendTextLine(string text, bool pressEnter)
        {
            var inputs = new List<INPUT>(text.Length * 2 + 2);
            for (int i = 0; i < text.Length; i++)
            {
                AddUnicodeChar(inputs, text[i]);
            }

            if (pressEnter)
            {
                AddVirtualKey(inputs, VK_RETURN);
            }

            if (inputs.Count == 0)
            {
                return false;
            }

            INPUT[] arr = inputs.ToArray();
            uint sent = SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
            return sent > 0;
        }

        private static void AddUnicodeChar(List<INPUT> inputs, char ch)
        {
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = ch,
                        dwFlags = KEYEVENTF_UNICODE,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero,
                    },
                },
            });
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = ch,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero,
                    },
                },
            });
        }

        private static void AddVirtualKey(List<INPUT> inputs, ushort vk)
        {
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = 0,
                        dwFlags = 0,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero,
                    },
                },
            });
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = 0,
                        dwFlags = KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero,
                    },
                },
            });
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public nint dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public nint dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(nint hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(nint hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(nint hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(nint hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(nint hWnd);

        private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        private static extern nint GetWindow(nint hWnd, uint uCmd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(nint hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern nint GetWindowLongPtr64(nint hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, [MarshalAs(UnmanagedType.LPArray), In] INPUT[] pInputs, int cbSize);
    }
}
