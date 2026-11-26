// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace Microsoft.PowerShell.Adb
{
    /// <summary>
    /// Initializes ADB device bindings and built-in PowerShell helper commands for SuperPowerShell sessions.
    /// </summary>
    internal static class AdbSessionInitializer
    {
        private const string PromptFunctionBody = @"
$esc = [char]27
$adbPrefix = if ($env:ANDROID_SERIAL) { ""${esc}[36m[ADB:$($env:ANDROID_SERIAL)]${esc}[0m "" } else { '' }
""${adbPrefix}PS $($executionContext.SessionState.Path.CurrentLocation)$('>' * ($nestedPromptLevel + 1)) ""
";

        private const string ShellFunctionBody = @"
if (-not $env:ANDROID_SERIAL) {
    Write-Warning '当前窗口未绑定 ADB 设备，请先运行 adb-use 选择设备。'
    return
}
if ($args.Count -eq 0) {
    & adb -s $env:ANDROID_SERIAL shell
}
else {
    & adb -s $env:ANDROID_SERIAL shell @args
}
";

        private const string AdbDevicesFunctionBody = @"
$lines = & adb devices 2>$null
$result = @()
foreach ($line in $lines) {
    if ($line -match '^(\S+)\s+device\s*$') {
        $result += $Matches[1]
    }
}
return $result
";

        private const string AdbUseFunctionBody = @"
param(
    [Parameter(Position = 0)]
    [string]$Serial,
    [switch]$Clear
)
if ($Clear) {
    Remove-Item Env:ANDROID_SERIAL -ErrorAction SilentlyContinue
    try { $Host.UI.RawUI.WindowTitle = 'SuperPowerShell' } catch {}
    Write-Host '已解除当前窗口 ADB 设备绑定。' -ForegroundColor Yellow
    return
}
if (-not $Serial) {
    $devices = @(adb-devices)
    if ($devices.Count -eq 0) {
        Write-Warning '未检测到在线 ADB 设备。'
        return
    }
    for ($i = 0; $i -lt $devices.Count; $i++) {
        Write-Host ""[$($i + 1)] $($devices[$i])""
    }
    $choice = Read-Host '请选择设备编号'
    $idx = 0
    if (-not [int]::TryParse($choice, [ref]$idx) -or $idx -lt 1 -or $idx -gt $devices.Count) {
        Write-Warning '无效选择。'
        return
    }
    $Serial = $devices[$idx - 1]
}
$env:ANDROID_SERIAL = $Serial
try { $Host.UI.RawUI.WindowTitle = $Serial } catch {}
Write-Host ""当前窗口已绑定 ADB 设备: $Serial"" -ForegroundColor Cyan
";

        private const string AdbOpenFunctionBody = @"
param(
    [Parameter(Position = 0)]
    [string]$Serial
)
if (-not $Serial) {
    $devices = @(adb-devices)
    if ($devices.Count -eq 0) {
        Write-Warning '未检测到在线 ADB 设备。'
        return
    }
    for ($i = 0; $i -lt $devices.Count; $i++) {
        Write-Host ""[$($i + 1)] $($devices[$i])""
    }
    $choice = Read-Host '请选择要新开窗口的设备编号'
    $idx = 0
    if (-not [int]::TryParse($choice, [ref]$idx) -or $idx -lt 1 -or $idx -gt $devices.Count) {
        Write-Warning '无效选择。'
        return
    }
    $Serial = $devices[$idx - 1]
}
$pwsh = [System.Environment]::ProcessPath
$wtCmd = Get-Command wt.exe -ErrorAction SilentlyContinue
if ($wtCmd) {
    & $wtCmd.Source -w 0 new-tab --title $Serial --suppressApplicationTitle -- $pwsh -NoLogo -AdbDevice $Serial
}
else {
    Start-Process -FilePath $pwsh -ArgumentList @('-NoLogo', '-AdbDevice', $Serial)
}
";

        private const string EnableAdbTerminalSyncFunctionBody = @"
$pwsh = [System.Environment]::ProcessPath
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Set-ItemProperty -Path $runKey -Name 'SuperPowerShellAdbWatch' -Value ""`""$pwsh`"" -AdbWatch""
Write-Host '已启用开机自动同步 Windows Terminal ADB 设备列表。' -ForegroundColor Green
";

        private const string DisableAdbTerminalSyncFunctionBody = @"
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Remove-ItemProperty -Path $runKey -Name 'SuperPowerShellAdbWatch' -ErrorAction SilentlyContinue
Write-Host '已关闭开机自动同步 Windows Terminal ADB 设备列表。' -ForegroundColor Yellow
";

        /// <summary>
        /// Applies ADB PATH fallback, ANDROID_SERIAL binding, and console title before Runspace startup.
        /// </summary>
        internal static void ApplyStartupBinding(string? adbDevice)
        {
            AdbLocator.EnsureAdbInPath();

            string? targetSerial = !string.IsNullOrWhiteSpace(adbDevice)
                ? adbDevice.Trim()
                : Environment.GetEnvironmentVariable("ANDROID_SERIAL");

            if (!string.IsNullOrWhiteSpace(targetSerial))
            {
                Environment.SetEnvironmentVariable("ANDROID_SERIAL", targetSerial);
                try
                {
                    Console.Title = targetSerial;
                }
                catch
                {
                    // Ignore when running without a console window.
                }
            }
        }

        /// <summary>
        /// Registers SuperPowerShell ADB helper functions into the InitialSessionState.
        /// </summary>
        internal static void RegisterBuiltInFunctions(InitialSessionState iss)
        {
            ReplaceOrAddFunction(iss, "prompt", PromptFunctionBody);
            ReplaceOrAddFunction(iss, "shell", ShellFunctionBody);
            ReplaceOrAddFunction(iss, "adb-devices", AdbDevicesFunctionBody);
            ReplaceOrAddFunction(iss, "adb-use", AdbUseFunctionBody);
            ReplaceOrAddFunction(iss, "adb-open", AdbOpenFunctionBody);
            ReplaceOrAddFunction(iss, "Enable-AdbTerminalSync", EnableAdbTerminalSyncFunctionBody);
            ReplaceOrAddFunction(iss, "Disable-AdbTerminalSync", DisableAdbTerminalSyncFunctionBody);
        }

        private static void ReplaceOrAddFunction(InitialSessionState iss, string name, string body)
        {
            iss.Commands.Remove(name, typeof(SessionStateFunctionEntry));
            iss.Commands.Add(new SessionStateFunctionEntry(name, body));
        }
    }
}
