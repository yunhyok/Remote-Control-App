using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using RemoteMonitorLink;

namespace RemoteMonitorSlave
{
    internal static class Program
    {
        internal const string Title = "Remote Monitor Slave v" + LinkVersion.Value;

        [STAThread]
        private static void Main(string[] args)
        {
            if (OutputBufferCapture.TryRunWorker(args)) return;
            if (PowerSiScreenCapture.TryRunWorker(args)) return;
            if (PowerSiObservation.TryRunWorker(args)) return;
            if (args.Length == 1 && args[0] == "--self-test")
            {
                try { LinkSelfTest.Run(); PowerSiOutputBuffer.SelfTest(); OutputBufferCapture.SelfTest(); SlaveForm.SelfTest(); Console.WriteLine("PASS: Slave status link and UI checks"); }
                catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
                return;
            }
            bool owns;
            using (var mutex = new Mutex(true, @"Local\RemoteMonitorSlave", out owns))
            {
                if (!owns) { MessageBox.Show("Slave가 이미 실행 중입니다.", Title); return; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try { Application.Run(new SlaveForm()); }
                catch (Exception ex) { MessageBox.Show("Slave 시작 실패: " + ex.GetType().Name, Title); }
            }
        }
    }

    internal sealed class SlaveLog
    {
        internal readonly string Path;
        private readonly object gate = new object();
        internal SlaveLog(string directory)
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "remote-monitor-slave-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") +
                "-pid" + System.Diagnostics.Process.GetCurrentProcess().Id + ".log");
            Write("APP_START");
        }
        internal void Write(string code)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(code, @"\A[A-Z0-9_]{1,64}\z")) code = "LINK_EVENT";
            lock (gate)
                File.AppendAllText(Path, DateTime.UtcNow.ToString("O") + " version=" + LinkVersion.Value + " code=" + code +
                    Environment.NewLine, new UTF8Encoding(false)); // Each event releases the stream immediately.
        }

        internal void WritePowerSi(PowerSiObservation observation)
        {
            var wire = PowerSiObservation.Parse(observation.Serialize()).Serialize();
            var stage = observation.ProbeStage;
            var prefix = DateTime.UtcNow.ToString("O") + " version=" + LinkVersion.Value;
            var entry = new StringBuilder(prefix + " code=PWRSI_OBSERVATION data=" + wire + " stage=" + stage + Environment.NewLine);
            if (observation.LocalSampleId != null)
                entry.Append(prefix).Append(" code=VISION_RUN").Append(ComparisonMetadata(observation)).Append(Environment.NewLine);
            if (observation.LocalFailure != null && System.Text.RegularExpressions.Regex.IsMatch(observation.LocalFailure, @"\A[A-Z0-9_]{1,64}\z"))
                entry.Append(prefix).Append(" code=PWRSI_LOCAL_FAILURE data=").Append(observation.LocalFailure).Append(Environment.NewLine);
            foreach (var detail in observation.ProbeDetails)
                entry.Append(prefix).Append(" code=PWRSI_PROBE data=").Append(detail).Append(Environment.NewLine);
            lock (gate)
                File.AppendAllText(Path, entry.ToString(), new UTF8Encoding(false));
        }

        private static string ComparisonMetadata(PowerSiObservation result)
        {
            string Id(string value) => value != null && System.Text.RegularExpressions.Regex.IsMatch(value, @"\A[A-F0-9]{64}\z") ? value : "UNKNOWN";
            string Label(string value) => string.IsNullOrWhiteSpace(value) || value.Length > 256 ? "UNKNOWN" : Uri.EscapeDataString(value);
            var model = result.LocalModelInfo;
            string geometry = result.LocalRegionInfo != null && System.Text.RegularExpressions.Regex.IsMatch(result.LocalRegionInfo, @"\AP2(\|[0-9]{1,5}){4}((\|[0-9]{1,5}){6})?\z")
                ? result.LocalRegionInfo : "UNKNOWN";
            return " mode=" + (result.LocalVisionMode == "OCR_ONLY" ? "OCR_ONLY crop_reused=1" : "LOCATE_OCR crop_reused=0") +
                " request_timeout_s=" + Math.Max(0, Math.Min(90, result.LocalRequestTimeoutSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " sample=" + Id(result.LocalSampleId) + " ocr_sample=" + Id(result.LocalOcrSampleId) +
                " model_id=" + Label(model?.Id) + " model_name=" + Label(model?.DisplayName) + " model_key=" + Label(model?.Key) +
                " quantization=" + Label(model?.Quantization) + " total_ms=" + Math.Max(0, result.LocalElapsedMs).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " locate_ms=" + Math.Max(0, result.LocalLocateMs).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " read_ms=" + Math.Max(0, result.LocalReadMs).ToString(System.Globalization.CultureInfo.InvariantCulture) + " geometry=" + geometry;
        }

        internal void WriteOutputBuffer(OutputBufferResult result)
        {
            var metadata = OutputBufferCapture.LogMetadata(result); // Raw buffer is never part of log metadata.
            lock (gate)
                File.AppendAllText(Path, DateTime.UtcNow.ToString("O") + " version=" + LinkVersion.Value +
                    " code=OUTPUT_BUFFER" + metadata + Environment.NewLine, new UTF8Encoding(false));
        }
    }
}
