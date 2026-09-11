using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RemoteMonitorLink;

namespace RemoteMonitorSlave
{
    // A local-only result pipe. Never reuse this encoding for the Master/messenger protocol.
    internal static class OutputBufferCapture
    {
        internal const int MaxCharacters = PowerSiOutputBuffer.MaxCharacters;
        private const int MaxWire = (MaxCharacters * 2 + 2) / 3 * 4 + 4096;
        private const string Argument = "--powersi-output-buffer";
        private const string CopyArgument = "--powersi-output-copy";
        private static readonly Encoding Encoding = new UnicodeEncoding(false, false, true);

        internal static bool TryRunWorker(string[] args)
        {
            if (args.Length == 0 || (args[0] != Argument && args[0] != CopyArgument)) return false;
            OutputBufferResult result;
            try
            {
                var targetArgs = args[0] == CopyArgument ? args.Take(3).ToArray() : args;
                var root = PowerSiScreenCapture.ResolveWindow(targetArgs);
                if (args[0] == CopyArgument)
                {
                    uint baseline;
                    if (args.Length != 4 || !uint.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out baseline))
                        throw new InvalidDataException("SC_IDENTITY");
                    var inventory = new ProcessInventory { SessionId = int.Parse(args[1], CultureInfo.InvariantCulture),
                        Items = args[2].Split(',').Select(p => p.Split(':')).Select(p => new ProcessState {
                            Pid = int.Parse(p[0], CultureInfo.InvariantCulture), StartUtcTicks = long.Parse(p[1], CultureInfo.InvariantCulture) }).ToArray() };
                    if (!TryReadUserCopy(inventory, baseline, out result)) result = Failed("COPY_PENDING");
                }
                else result = PowerSiOutputBuffer.Read(root);
                if (PowerSiScreenCapture.ResolveWindow(targetArgs) != root) throw new InvalidDataException("SC_WINDOW_CHANGED");
            }
            catch (InvalidDataException ex) { result = Failed(SafeCode(ex.Message) ? ex.Message : "BUFFER_FAILED"); }
            catch { result = Failed("BUFFER_FAILED"); }
            try { Console.Out.Write(Serialize(result)); }
            catch { Console.Out.Write(Serialize(Failed("BUFFER_TEXT_INVALID"))); }
            return true;
        }

        internal static async Task<OutputBufferResult> ReadAsync(ProcessInventory inventory, CancellationToken cancellation, uint? copyBaseline = null)
        {
            inventory.Validate();
            if (inventory.Items.Length == 0) return Failed("SC_NOT_RUNNING");
            if (inventory.Omitted != 0 || inventory.Items.Any(p => !ProcessInventory.IsPowerSiName(p.Name) || !p.StartUtcTicks.HasValue))
                return Failed("SC_IDENTITY");
            var identities = string.Join(",", inventory.Items.Select(p => p.Pid.ToString(CultureInfo.InvariantCulture) + ":" +
                p.StartUtcTicks.Value.ToString(CultureInfo.InvariantCulture)));
            using (var worker = new Process { StartInfo = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                (copyBaseline.HasValue ? CopyArgument : Argument) + " " + inventory.SessionId.ToString(CultureInfo.InvariantCulture) + " " + identities +
                    (copyBaseline.HasValue ? " " + copyBaseline.Value.ToString(CultureInfo.InvariantCulture) : ""))
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true } })
            {
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    worker.Start();
                    var reading = ReadBounded(worker.StandardOutput);
                    var clock = Stopwatch.StartNew();
                    while (!worker.HasExited || !reading.IsCompleted)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (reading.IsFaulted) await reading.ConfigureAwait(false);
                        if (clock.ElapsedMilliseconds >= (copyBaseline.HasValue ? 3000 : 10000)) return Failed("BUFFER_TIMEOUT");
                        await Task.Delay(25, cancellation).ConfigureAwait(false);
                    }
                    cancellation.ThrowIfCancellationRequested();
                    return worker.ExitCode == 0 ? Parse(await reading.ConfigureAwait(false)) : Failed("BUFFER_WORKER_FAILED");
                }
                catch (OperationCanceledException) { throw; }
                catch { return Failed("BUFFER_WORKER_FAILED"); }
                finally { try { if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(200); } } catch { } }
            }
        }

        private static async Task<string> ReadBounded(StreamReader reader)
        {
            var text = new StringBuilder(); var buffer = new char[8192];
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
            {
                if (text.Length + count > MaxWire) throw new InvalidDataException("BUFFER_SIZE");
                text.Append(buffer, 0, count);
            }
            return text.ToString();
        }

        private static OutputBufferResult Failed(string code) { return new OutputBufferResult { Code = code, Method = "NONE", Detail = "NONE" }; }
        private static bool SafeCode(string text) { return text != null && Regex.IsMatch(text, @"\A[A-Z0-9_]{1,64}\z"); }
        private static void Validate(OutputBufferResult result)
        {
            if (result == null || !SafeCode(result.Code) || !SafeCode(result.Method) || result.Detail == null ||
                !Regex.IsMatch(result.Detail, @"\A[A-Z0-9_:=,| -]{1,512}\z") || (result.Text?.Length ?? 0) > MaxCharacters)
                throw new InvalidDataException("BUFFER_RESULT_INVALID");
        }
        private static string Serialize(OutputBufferResult result)
        {
            Validate(result);
            return string.Join("\t", "OB1", result.Code, result.Method, result.Detail,
                result.Text == null ? "-" : Convert.ToBase64String(Encoding.GetBytes(result.Text)));
        }
        private static OutputBufferResult Parse(string wire)
        {
            if (wire == null || wire.Length > MaxWire) throw new InvalidDataException("BUFFER_SIZE");
            var parts = wire.Split('\t');
            if (parts.Length != 5 || parts[0] != "OB1") throw new InvalidDataException("BUFFER_RESULT_INVALID");
            var bytes = parts[4] == "-" ? new byte[0] : Convert.FromBase64String(parts[4]);
            if (bytes.Length > MaxCharacters * 2 || (parts[4] != "-" && Convert.ToBase64String(bytes) != parts[4])) throw new InvalidDataException("BUFFER_SIZE");
            var result = new OutputBufferResult { Code = parts[1], Method = parts[2], Detail = parts[3], Text = parts[4] == "-" ? null : Encoding.GetString(bytes) };
            Validate(result);
            result.CharacterCount = result.Text?.Length ?? 0;
            result.LineCount = CountLines(result.Text);
            return result;
        }
        internal static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int count = 1;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n' || (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n'))) count++;
            return count;
        }

        internal static uint ClipboardSequence { get { return GetClipboardSequenceNumber(); } }
        // Optional same-run fallback: user copies Output. We only read a NEW clipboard value owned by the identified PowerSI.
        // No EmptyClipboard/SetClipboardData, clipboard replacement, keyboard input or mouse automation here.
        internal static bool TryReadUserCopy(ProcessInventory inventory, uint baseline, out OutputBufferResult result)
        {
            result = null;
            uint sequence = GetClipboardSequenceNumber();
            if (sequence == 0 || sequence == baseline || !OpenClipboard(IntPtr.Zero)) return false;
            try
            {
                if (GetClipboardSequenceNumber() != sequence) return false;
                var owner = GetClipboardOwner(); uint pid;
                if (owner == IntPtr.Zero || GetWindowThreadProcessId(owner, out pid) == 0) return false;
                var identity = inventory.Items.SingleOrDefault(p => p.Pid == (int)pid);
                if (identity == null || !identity.StartUtcTicks.HasValue) return false;
                using (var process = Process.GetProcessById(identity.Pid))
                    if (process.HasExited || process.SessionId != inventory.SessionId ||
                        process.StartTime.ToUniversalTime().Ticks != identity.StartUtcTicks.Value ||
                        !ProcessInventory.IsPowerSiName(ProcessInventory.NormalizeName(process.ProcessName))) return false;
                var memory = GetClipboardData(13); // CF_UNICODETEXT; Windows supplies standard ANSI conversion when supported.
                if (memory == IntPtr.Zero) return false;
                ulong bytes = GlobalSize(memory).ToUInt64();
                if (bytes < 2 || bytes > (ulong)(MaxCharacters + 1) * 2) { result = Failed("BUFFER_SIZE"); return true; }
                var pointer = GlobalLock(memory);
                if (pointer == IntPtr.Zero) return false;
                try
                {
                    int count = 0, bound = (int)(bytes / 2);
                    while (count < bound && Marshal.ReadInt16(pointer, count * 2) != 0) count++;
                    if (count == bound || count > MaxCharacters) { result = Failed("BUFFER_SIZE"); return true; }
                    string text = Marshal.PtrToStringUni(pointer, count);
                    if (string.IsNullOrWhiteSpace(text)) return false;
                    result = new OutputBufferResult { Text = text, Code = "USER_COPY_READ", Method = "USER_CLIPBOARD",
                        Detail = "SOURCE_PID_MATCH", CharacterCount = text.Length, LineCount = CountLines(text) };
                    return true;
                }
                finally { GlobalUnlock(memory); }
            }
            catch { return false; }
            finally { CloseClipboard(); }
        }

        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr window);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr memory);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
        internal static string LogMetadata(OutputBufferResult result)
        {
            Validate(result);
            return " result=" + result.Code + " method=" + result.Method + " chars=" + (result.Text?.Length ?? 0).ToString(CultureInfo.InvariantCulture) +
                " lines=" + CountLines(result.Text).ToString(CultureInfo.InvariantCulture) + " detail=" + result.Detail;
        }
        internal static void SelfTest()
        {
            OutputBufferResult stale;
            if (TryReadUserCopy(new ProcessInventory(), ClipboardSequence, out stale))
                throw new InvalidOperationException("Unidentified/stale clipboard accepted as PowerSI Output.");
            var result = new OutputBufferResult { Code = "BUFFER_READ", Method = "NATIVE_WM_GETTEXT", Detail = "NATIVE=1,UIA=0",
                Text = "private-buffer-sentinel\r\nAFS Current Frequency (MHz) = 860.000\n완료\r끝" };
            var copy = Parse(Serialize(result));
            if (Parse(Serialize(Failed("BUFFER_STANDARD_TEXT_NOT_FOUND"))).Text != null)
                throw new InvalidOperationException("Unavailable buffer became empty successful text.");
            if (copy.Text != result.Text || copy.LineCount != 4 || copy.CharacterCount != result.Text.Length ||
                LogMetadata(copy).Contains("private") || LogMetadata(copy).Contains("860.000"))
                throw new InvalidOperationException("Buffer pipe preservation/privacy failed.");
            result.Text = new string('x', MaxCharacters + 1);
            try { Serialize(result); } catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Oversize buffer silently accepted.");
        }
    }
}
