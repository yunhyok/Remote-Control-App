using System;
using System.Threading;
using System.Windows.Forms;

namespace RemoteMonitorMaster
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length == 1 && string.Equals(args[0], "--self-test", StringComparison.Ordinal))
            {
                Environment.ExitCode = CoreSelfTest.Run();
                return;
            }

            bool ownsInstance;
            using (var instance = new Mutex(true, AppInfo.InstanceMutexName, out ownsInstance))
            {
                if (!ownsInstance)
                {
                    MessageBox.Show("Remote Monitor Master is already running in this Windows session.",
                        AppInfo.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                AuditLog log;
                try
                {
                    log = new AuditLog();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        "Remote Monitor Master stopped safely.\r\n\r\nReason: " + ex.GetType().Name +
                        "\r\nLog unavailable.",
                        AppInfo.Title,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return;
                }

                using (log)
                {
                    try
                    {
                        log.Write("INFO", "APP_START",
                            AuditLog.Field("os", Environment.OSVersion.VersionString),
                            AuditLog.Field("clr", Environment.Version),
                            AuditLog.Field("framework_release", SystemInfo.FrameworkRelease),
                            AuditLog.Field("process_64bit", Environment.Is64BitProcess),
                            AuditLog.Field("os_64bit", Environment.Is64BitOperatingSystem));
                        Application.Run(new MainForm(log));
                        log.Write("INFO", "APP_EXIT");
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            log.WriteException("APP_FATAL", ex);
                        }
                        catch
                        {
                            // A logging failure must not hide the safe-stop dialog.
                        }

                        MessageBox.Show(
                            "Remote Monitor Master stopped safely.\r\n\r\nReason: " + ex.GetType().Name +
                            "\r\nLog: " + log.FilePath,
                            AppInfo.Title,
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }
                }
            }
        }
    }
}
