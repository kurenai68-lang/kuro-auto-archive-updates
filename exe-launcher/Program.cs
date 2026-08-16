using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONERROR = 0x00000010;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            bool worker = args.Any(a => string.Equals(a, "--worker", StringComparison.OrdinalIgnoreCase));
            bool uninstall = args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase));
            bool autoStart = args.Any(a => string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase));

            string scriptName = uninstall
                ? "Uninstall_KURO_Auto_Archive.ps1"
                : worker ? "worker.ps1" : "TwitchAutoArchive.ps1";

            string scriptPath = Path.Combine(baseDir, scriptName);
            if (!File.Exists(scriptPath))
            {
                ShowError($"必要なファイルが見つかりません。\n\n{scriptPath}");
                return 2;
            }

            string systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string psPath = Path.Combine(systemDir, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(psPath))
                psPath = "powershell.exe";

            string psArgs = "-NoProfile -ExecutionPolicy Bypass ";
            if (!worker && !uninstall)
                psArgs += "-STA ";
            psArgs += $"-File \"{scriptPath}\"";
            if (autoStart && !worker && !uninstall)
                psArgs += " -AutoStart";

            var psi = new ProcessStartInfo
            {
                FileName = psPath,
                Arguments = psArgs,
                WorkingDirectory = baseDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using Process? child = Process.Start(psi);
            if (child == null)
            {
                ShowError("KURO Auto Archive を起動できませんでした。");
                return 3;
            }

            child.WaitForExit();
            return child.ExitCode;
        }
        catch (Exception ex)
        {
            ShowError("KURO Auto Archive の起動中にエラーが発生しました。\n\n" + ex.Message);
            return 1;
        }
    }

    private static void ShowError(string message)
    {
        try { MessageBoxW(IntPtr.Zero, message, "KURO Auto Archive", MB_OK | MB_ICONERROR); }
        catch { }
    }
}
