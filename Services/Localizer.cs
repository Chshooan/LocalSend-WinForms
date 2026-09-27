using System.Globalization;

namespace LocalSendWinForms.Services;

/// <summary>
/// Lightweight runtime localization. Two shipped languages ("zh-CN" and "en")
/// cover the whole UI; unknown keys fall back to English and then to the key
/// itself so a missing entry can never crash or blank the interface.
/// </summary>
public static class Localizer
{
    public const string Chinese = "zh-CN";
    public const string English = "en";

    public static event Action? LanguageChanged;

    private static string _current = Chinese;

    public static string Current => _current;

    public static void SetLanguage(string? language)
    {
        var normalized = Normalize(language);
        if (normalized == _current) return;
        _current = normalized;
        LanguageChanged?.Invoke();
    }

    public static string Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return Chinese;
        var l = language.ToLowerInvariant();
        if (l.StartsWith("en")) return English;
        return Chinese;
    }

    /// <summary>Looks up a string in the active language.</summary>
    public static string T(string key)
    {
        if (Table.TryGetValue(_current, out var map) && map.TryGetValue(key, out var value))
            return value;
        if (Table.TryGetValue(English, out var en) && en.TryGetValue(key, out var fallback))
            return fallback;
        return key;
    }

    /// <summary>Looks up and formats a string using the current culture.</summary>
    public static string T(string key, params object[] args)
    {
        var format = T(key);
        try { return string.Format(CultureInfo.CurrentCulture, format, args); }
        catch (FormatException) { return format; }
    }

    private static readonly Dictionary<string, Dictionary<string, string>> Table = new()
    {
        [English] = new Dictionary<string, string>
        {
            // Application / tray
            ["App.Name"] = "LocalSend for Windows",
            ["App.Tagline"] = "Share files over your local network",
            ["Tray.Open"] = "Open",
            ["Tray.QuickSend"] = "Quick send…",
            ["Tray.Settings"] = "Settings",
            ["Tray.OpenFolder"] = "Open transfer folder",
            ["Tray.About"] = "About / version",
            ["Tray.Exit"] = "Exit",
            ["Tray.Tooltip"] = "LocalSend — click to open",

            // Main window
            ["Main.Title"] = "LocalSend — Send files",
            ["Main.Devices"] = "Nearby devices",
            ["Main.Refresh"] = "Refresh",
            ["Main.Files"] = "Files to send",
            ["Main.AddFiles"] = "Add files",
            ["Main.AddFolder"] = "Add folder",
            ["Main.Remove"] = "Remove",
            ["Main.Clear"] = "Clear",
            ["Main.Send"] = "Send",
            ["Main.Settings"] = "Settings",
            ["Main.NoDevices"] = "Searching for devices…",
            ["Main.DropHint"] = "Drag files here, or use Add files",
            ["Main.SelectDevice"] = "Select a target device first.",
            ["Main.NoFiles"] = "Add at least one file to send.",
            ["Main.DevicesFound"] = "{0} device(s) found",
            ["Main.Searching"] = "Searching…",
            ["Main.Sending"] = "Sending…",
            ["Main.Ready"] = "Ready",

            // Settings
            ["Settings.Title"] = "Settings",
            ["Settings.General"] = "General",
            ["Settings.DeviceName"] = "Device name",
            ["Settings.DownloadDir"] = "Default save folder",
            ["Settings.Browse"] = "Browse…",
            ["Settings.OpenFolder"] = "Open folder",
            ["Settings.Port"] = "Port",
            ["Settings.UseHttps"] = "Encrypt transfers with HTTPS",
            ["Settings.Language"] = "Language",
            ["Settings.Receive"] = "Receiving",
            ["Settings.AutoAccept"] = "Auto-accept incoming files (no confirmation)",
            ["Settings.Conflict"] = "Name conflicts",
            ["Settings.ConflictRename"] = "Rename automatically (recommended)",
            ["Settings.ConflictOverwrite"] = "Overwrite existing files",
            ["Settings.Pin"] = "PIN required from senders (optional)",
            ["Settings.Notify"] = "Notifications",
            ["Settings.NotifyReceive"] = "Notify when a transfer is offered",
            ["Settings.NotifyComplete"] = "Notify when a transfer completes",
            ["Settings.MinimizeTray"] = "Minimize to tray when the window is closed",
            ["Settings.LaunchStartup"] = "Start with Windows",
            ["Settings.ComputeSha256"] = "Verify SHA-256 checksums when sending",
            ["Settings.Explorer"] = "File Explorer integration",
            ["Settings.InstallMenu"] = "Add “Send with LocalSend” to the right-click menu",
            ["Settings.RemoveMenu"] = "Remove right-click menu entry",
            ["Settings.Save"] = "Save",
            ["Settings.Cancel"] = "Cancel",
            ["Settings.Saved"] = "Settings saved.",
            ["Settings.LanguageNote"] = "Changing the language takes effect immediately.",

            // Receive confirmation
            ["Receive.Title"] = "Incoming file transfer",
            ["Receive.From"] = "From",
            ["Receive.Prompt"] = "wants to send you the following files:",
            ["Receive.File"] = "File",
            ["Receive.Size"] = "Size",
            ["Receive.Accept"] = "Accept",
            ["Receive.Reject"] = "Decline",
            ["Receive.SelectAll"] = "Select all",
            ["Receive.DeselectAll"] = "Select none",
            ["Receive.AutoClose"] = "This request will be declined automatically in {0} s.",

            // Progress window
            ["Progress.Title"] = "Transfer progress",
            ["Progress.To"] = "Sending to {0}",
            ["Progress.From"] = "Receiving from {0}",
            ["Progress.Cancel"] = "Cancel",
            ["Progress.Close"] = "Close",
            ["Progress.Done"] = "Transfer complete",
            ["Progress.Failed"] = "Some files failed to transfer",
            ["Progress.HandshakeFailed"] = "Could not reach the peer (TLS handshake failed). Check the device name/port, or turn off HTTPS in settings.",
            ["Progress.Cancelled"] = "Transfer cancelled",
            ["Progress.Overall"] = "Overall",
            ["Progress.Speed"] = "{0}/s",

            // About
            ["About.Title"] = "About",
            ["About.Version"] = "Version {0}",
            ["About.Description"] =
                "A native Windows client for the open LocalSend v2 protocol, providing " +
                "automatic device discovery and encrypted peer-to-peer file transfer on your LAN.",
            ["About.Protocol"] = "Protocol: LocalSend v2 (224.0.0.167:53317)",
            ["About.License"] = "Licensed under the MIT License.",
            ["About.OpenLog"] = "Open log folder",
            ["About.Project"] = "LocalSend protocol project",

            // Notifications
            ["Notif.ReceiveTitle"] = "Incoming file transfer",
            ["Notif.ReceiveBody"] = "{0} wants to send {1} file(s).",
            ["Notif.CompleteTitle"] = "Transfer complete",
            ["Notif.CompleteBody"] = "Received {0} file(s) into {1}.",
            ["Notif.SentTitle"] = "Files sent",
            ["Notif.SentBody"] = "Sent {0} file(s) to {1}.",
            ["Notif.FailedTitle"] = "Transfer failed",
            ["Notif.FailedBody"] = "{0} file(s) could not be sent to {1}.",

            // Messages
            ["Msg.Error"] = "Error",
            ["Msg.SendFailed"] = "Send failed",
            ["Msg.DeviceOffline"] = "The device may be offline.",
            ["Msg.MenuInstalled"] = "Added to the Explorer right-click menu.",
            ["Msg.MenuRemoved"] = "Removed from the Explorer right-click menu.",
            ["Msg.NothingReceived"] = "Nothing was received.",
        },

        [Chinese] = new Dictionary<string, string>
        {
            // Application / tray
            ["App.Name"] = "LocalSend for Windows",
            ["App.Tagline"] = "在局域网中安全互传文件",
            ["Tray.Open"] = "打开主界面",
            ["Tray.QuickSend"] = "快速发送…",
            ["Tray.Settings"] = "设置",
            ["Tray.OpenFolder"] = "打开传输文件夹",
            ["Tray.About"] = "版本信息",
            ["Tray.Exit"] = "退出",
            ["Tray.Tooltip"] = "LocalSend — 点击打开主界面",

            // Main window
            ["Main.Title"] = "LocalSend — 文件发送",
            ["Main.Devices"] = "附近的设备",
            ["Main.Refresh"] = "刷新",
            ["Main.Files"] = "待发送文件",
            ["Main.AddFiles"] = "添加文件",
            ["Main.AddFolder"] = "添加文件夹",
            ["Main.Remove"] = "移除",
            ["Main.Clear"] = "清空",
            ["Main.Send"] = "发送",
            ["Main.Settings"] = "设置",
            ["Main.NoDevices"] = "正在搜索附近的设备…",
            ["Main.DropHint"] = "将文件拖到此处,或点击“添加文件”",
            ["Main.SelectDevice"] = "请先选择目标设备。",
            ["Main.NoFiles"] = "请先添加至少一个要发送的文件。",
            ["Main.DevicesFound"] = "已发现 {0} 台设备",
            ["Main.Searching"] = "搜索中…",
            ["Main.Sending"] = "发送中…",
            ["Main.Ready"] = "就绪",

            // Settings
            ["Settings.Title"] = "设置",
            ["Settings.General"] = "常规",
            ["Settings.DeviceName"] = "设备名称",
            ["Settings.DownloadDir"] = "默认保存路径",
            ["Settings.Browse"] = "浏览…",
            ["Settings.OpenFolder"] = "打开文件夹",
            ["Settings.Port"] = "端口",
            ["Settings.UseHttps"] = "使用 HTTPS 加密传输",
            ["Settings.Language"] = "界面语言",
            ["Settings.Receive"] = "接收",
            ["Settings.AutoAccept"] = "自动接收文件(无需确认)",
            ["Settings.Conflict"] = "重名文件处理",
            ["Settings.ConflictRename"] = "自动重命名(推荐)",
            ["Settings.ConflictOverwrite"] = "覆盖已有文件",
            ["Settings.Pin"] = "接收 PIN 码(可选)",
            ["Settings.Notify"] = "通知",
            ["Settings.NotifyReceive"] = "收到传输请求时通知",
            ["Settings.NotifyComplete"] = "传输完成时通知",
            ["Settings.MinimizeTray"] = "关闭窗口时最小化到系统托盘",
            ["Settings.LaunchStartup"] = "开机自动启动",
            ["Settings.ComputeSha256"] = "发送时校验 SHA-256",
            ["Settings.Explorer"] = "资源管理器集成",
            ["Settings.InstallMenu"] = "在右键菜单添加“用 LocalSend 发送”",
            ["Settings.RemoveMenu"] = "从右键菜单移除",
            ["Settings.Save"] = "保存",
            ["Settings.Cancel"] = "取消",
            ["Settings.Saved"] = "设置已保存。",
            ["Settings.LanguageNote"] = "切换语言后立即生效。",

            // Receive confirmation
            ["Receive.Title"] = "收到文件传输请求",
            ["Receive.From"] = "来自",
            ["Receive.Prompt"] = "希望向你发送以下文件:",
            ["Receive.File"] = "文件",
            ["Receive.Size"] = "大小",
            ["Receive.Accept"] = "接收",
            ["Receive.Reject"] = "拒绝",
            ["Receive.SelectAll"] = "全选",
            ["Receive.DeselectAll"] = "全不选",
            ["Receive.AutoClose"] = "若未操作,该请求将在 {0} 秒后自动拒绝。",

            // Progress window
            ["Progress.Title"] = "传输进度",
            ["Progress.To"] = "正在发送到 {0}",
            ["Progress.From"] = "正在接收来自 {0}",
            ["Progress.Cancel"] = "取消",
            ["Progress.Close"] = "关闭",
            ["Progress.Done"] = "传输完成",
            ["Progress.Failed"] = "部分文件传输失败",
            ["Progress.HandshakeFailed"] = "无法连接到对端（TLS 握手失败）。请检查设备名称/端口，或在设置中关闭 HTTPS。",
            ["Progress.Cancelled"] = "传输已取消",
            ["Progress.Overall"] = "总进度",
            ["Progress.Speed"] = "{0}/秒",

            // About
            ["About.Title"] = "关于",
            ["About.Version"] = "版本 {0}",
            ["About.Description"] =
                "这是一款基于开源 LocalSend v2 协议的原生 Windows 客户端,支持局域网内设备自动发现与加密点对点文件互传。",
            ["About.Protocol"] = "协议:LocalSend v2(224.0.0.167:53317)",
            ["About.License"] = "基于 MIT 许可证发布。",
            ["About.OpenLog"] = "打开日志文件夹",
            ["About.Project"] = "LocalSend 协议项目",

            // Notifications
            ["Notif.ReceiveTitle"] = "收到文件传输请求",
            ["Notif.ReceiveBody"] = "{0} 希望向你发送 {1} 个文件。",
            ["Notif.CompleteTitle"] = "传输完成",
            ["Notif.CompleteBody"] = "已接收 {0} 个文件,保存到 {1}。",
            ["Notif.SentTitle"] = "发送完成",
            ["Notif.SentBody"] = "已向 {0} 发送 {1} 个文件。",
            ["Notif.FailedTitle"] = "传输失败",
            ["Notif.FailedBody"] = "有 {0} 个文件未能发送到 {1}。",

            // Messages
            ["Msg.Error"] = "错误",
            ["Msg.SendFailed"] = "发送失败",
            ["Msg.DeviceOffline"] = "设备可能已离线。",
            ["Msg.MenuInstalled"] = "已添加到资源管理器右键菜单。",
            ["Msg.MenuRemoved"] = "已从资源管理器右键菜单移除。",
            ["Msg.NothingReceived"] = "未接收到任何文件。",
        },
    };
}
