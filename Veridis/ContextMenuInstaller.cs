using Microsoft.Win32;

public static class ContextMenuInstaller
{
    // Where we add the verb for PDFs (per-user; shows under “Show more options” on Win11)
    private const string PdfVerbKey = @"Software\Classes\SystemFileAssociations\.pdf\shell\FixInvoice";
    private const string PdfCmdKey = @"Software\Classes\SystemFileAssociations\.pdf\shell\FixInvoice\command";

    public static void Install(string exePath)
    {
        // Ensure quoted path + "%1"
        string quotedExe = $"\"{exePath}\"";
        string command = $"{quotedExe} \"%1\"";

        using (var shellKey = Registry.CurrentUser.CreateSubKey(PdfVerbKey))
        {
            shellKey!.SetValue(null, "Fix Invoice");                 // default value (menu text)
            shellKey.SetValue("Icon", $"{quotedExe},0");             // use your EXE icon
        }
        using (var cmdKey = Registry.CurrentUser.CreateSubKey(PdfCmdKey))
        {
            cmdKey!.SetValue(null, command);                         // default value is the command
        }

        // Optional: force Explorer to refresh its cache so the item appears immediately
        BroadcastShellChange();
    }

    public static void Uninstall()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(PdfVerbKey, throwOnMissingSubKey: false);
            BroadcastShellChange();
        }
        catch { /* swallow */ }
    }

    public static bool IsInstalled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(PdfCmdKey, writable: false);
        return k != null;
    }

    private static void BroadcastShellChange()
    {
        // Tells Explorer to refresh context menu registrations
        const int HWND_BROADCAST = 0xffff;
        const int WM_SETTINGCHANGE = 0x001A;
        NativeMethods.SendNotifyMessage((IntPtr)HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, IntPtr.Zero);
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = false, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        internal static extern bool SendNotifyMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
    }
}
