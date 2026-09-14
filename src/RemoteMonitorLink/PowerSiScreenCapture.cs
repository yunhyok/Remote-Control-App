using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteMonitorLink
{
    internal sealed class PowerSiFrame
    {
        internal byte[] Png;
        internal DateTime CapturedUtc;
        internal Size PixelSize;
    }

    internal static class PowerSiScreenCapture
    {
        private const string WorkerArgument = "--powersi-screen";
        private const int MaxPngBytes = 8 * 1024 * 1024;
        private const int MaxWireChars = ((MaxPngBytes + 2) / 3) * 4 + 64;
        private static readonly string[] Errors = { "SC_IDENTITY", "SC_NOT_RUNNING", "SC_WINDOW_UNAVAILABLE",
            "SC_AMBIGUOUS_WINDOW", "SC_MINIMIZED", "SC_DESKTOP_UNAVAILABLE", "SC_CAPTURE_FAILED", "SC_BLANK",
            "SC_SIZE", "SC_WINDOW_CHANGED", "SC_TIMEOUT", "SC_WORKER_FAILED", "SC_INVALID_IMAGE" };

        internal static async Task<PowerSiFrame> CaptureAsync(ProcessInventory inventory, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (inventory == null) throw Failure("SC_IDENTITY");
            inventory.Validate();
            if (inventory.Items.Length == 0) throw Failure("SC_NOT_RUNNING");
            if (inventory.Omitted != 0 || inventory.Items.Any(p => !ProcessInventory.IsPowerSiName(p.Name) ||
                !p.StartUtcTicks.HasValue || p.StartUtcTicks.Value <= 0)) throw Failure("SC_IDENTITY");
            var identities = string.Join(",", inventory.Items.Select(p => p.Pid.ToString(CultureInfo.InvariantCulture) + ":" +
                p.StartUtcTicks.Value.ToString(CultureInfo.InvariantCulture)));
            return await RunWorker(inventory.SessionId.ToString(CultureInfo.InvariantCulture) + " " + identities, cancellation).ConfigureAwait(false);
        }

        private static async Task<PowerSiFrame> RunWorker(string arguments, CancellationToken cancellation)
        {
            using (var worker = new Process { StartInfo = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                WorkerArgument + " " + arguments) { UseShellExecute = false, CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true } })
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
                        if (clock.ElapsedMilliseconds >= 4000) throw Failure("SC_TIMEOUT");
                        await Task.Delay(25, cancellation).ConfigureAwait(false);
                    }
                    cancellation.ThrowIfCancellationRequested();
                    if (worker.ExitCode != 0) throw Failure("SC_WORKER_FAILED");
                    return Parse(await reading.ConfigureAwait(false));
                }
                catch (OperationCanceledException) { throw; }
                catch (InvalidDataException error) when (Errors.Contains(error.Message)) { throw; }
                catch { throw Failure("SC_WORKER_FAILED"); }
                finally
                {
                    // Only this single-use helper is terminated; the observed application is never killed.
                    try { if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(200); } } catch { }
                }
            }
        }

        private static async Task<string> ReadBounded(StreamReader reader)
        {
            var text = new StringBuilder(); var buffer = new char[8192];
            while (true)
            {
                var count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (count == 0) return text.ToString();
                if (text.Length + count > MaxWireChars) throw Failure("SC_SIZE");
                text.Append(buffer, 0, count);
            }
        }

        internal static bool TryRunWorker(string[] args)
        {
            if (args.Length == 0 || args[0] != WorkerArgument) return false;
            try
            {
                var frame = ReadWorker(args);
                Console.Out.Write("SC1|" + frame.CapturedUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + Convert.ToBase64String(frame.Png));
            }
            catch (InvalidDataException error) when (Errors.Contains(error.Message)) { Console.Out.Write(error.Message); }
            catch { Console.Out.Write("SC_CAPTURE_FAILED"); }
            return true;
        }

        private static PowerSiFrame ReadWorker(string[] args)
        {
            var window = ResolveWindow(args);
            var frame = CaptureWindow(window);
            if (ResolveWindow(args) != window) throw Failure("SC_WINDOW_CHANGED");
            return frame;
        }

        // Shared target identity, independent of screenshot or text collection. Single window is the current field-test scope.
        internal static IntPtr ResolveWindow(string[] args)
        {
            int session;
            if (args.Length != 3 || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out session) ||
                session < 0 || args[2].Length > 4096) throw Failure("SC_IDENTITY");
            var identities = new Dictionary<int, long>();
            foreach (var item in args[2].Split(','))
            {
                var pair = item.Split(':'); int pid; long ticks;
                if (pair.Length != 2 || !int.TryParse(pair[0], NumberStyles.None, CultureInfo.InvariantCulture, out pid) || pid < 1 ||
                    !long.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out ticks) || ticks < 1 ||
                    ticks > DateTime.MaxValue.Ticks || identities.ContainsKey(pid)) throw Failure("SC_IDENTITY");
                identities.Add(pid, ticks);
            }
            if (identities.Count == 0 || identities.Count > ProcessInventory.MaxItems) throw Failure("SC_IDENTITY");
            CheckIdentities(identities, session);
            CheckDesktop(session);
            return FindWindow(identities);
        }

        private static void CheckIdentities(Dictionary<int, long> identities, int session)
        {
            using (var self = Process.GetCurrentProcess()) if (self.SessionId != session) throw Failure("SC_IDENTITY");
            foreach (var identity in identities)
            {
                using (var process = Process.GetProcessById(identity.Key))
                    if (process.HasExited || process.SessionId != session ||
                        !ProcessInventory.IsPowerSiName(ProcessInventory.NormalizeName(process.ProcessName)) ||
                        process.StartTime.ToUniversalTime().Ticks != identity.Value) throw Failure("SC_IDENTITY");
            }
            // Also reject a new PowerSI instance that was absent from the inventory snapshot.
            foreach (var name in new[] { "powersi", "pwrsi" })
            {
                var processes = Process.GetProcessesByName(name);
                try
                {
                    foreach (var process in processes)
                        if (process.SessionId == session && !identities.ContainsKey(process.Id)) throw Failure("SC_IDENTITY");
                }
                finally { foreach (var process in processes) process.Dispose(); }
            }
        }

        private static IntPtr FindWindow(Dictionary<int, long> identities)
        {
            var windows = new List<IntPtr>();
            var enumerated = EnumWindows((window, ignored) =>
            {
                uint pid; GetWindowThreadProcessId(window, out pid);
                if (identities.ContainsKey((int)pid) && IsWindowVisible(window)) windows.Add(window);
                return windows.Count < 2;
            }, IntPtr.Zero);
            if (windows.Count > 1) throw Failure("SC_AMBIGUOUS_WINDOW");
            if (!enumerated || windows.Count == 0) throw Failure("SC_WINDOW_UNAVAILABLE");
            if (IsIconic(windows[0])) throw Failure("SC_MINIMIZED");
            int cloaked;
            if (DwmGetWindowAttribute(windows[0], 14, out cloaked, 4) == 0 && cloaked != 0)
                throw Failure("SC_WINDOW_UNAVAILABLE");
            return windows[0];
        }

        private static void CheckDesktop(int session)
        {
            IntPtr state; int bytes;
            if (!WTSQuerySessionInformation(IntPtr.Zero, session, 8, out state, out bytes)) throw Failure("SC_DESKTOP_UNAVAILABLE");
            try { if (bytes < 4 || Marshal.ReadInt32(state) != 0) throw Failure("SC_DESKTOP_UNAVAILABLE"); }
            finally { WTSFreeMemory(state); }
            var desktop = OpenInputDesktop(0, false, 1); // DESKTOP_READOBJECTS; never switch or unlock a desktop.
            if (desktop == IntPtr.Zero) throw Failure("SC_DESKTOP_UNAVAILABLE");
            try
            {
                var name = new StringBuilder(256); uint needed;
                if (!GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out needed) ||
                    !string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase)) throw Failure("SC_DESKTOP_UNAVAILABLE");
                name.Clear();
                if (!GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, name, name.Capacity * 2, out needed) ||
                    !string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase)) throw Failure("SC_DESKTOP_UNAVAILABLE");
            }
            finally { CloseDesktop(desktop); }
        }

        // Internal so the Slave auto-copy worker can reuse the same visibility/size/blank rules in its own process
        // instead of spawning a second capture worker. Throws InvalidDataException with an SC_* code on failure.
        internal static PowerSiFrame CaptureWindow(IntPtr window)
        {
            Rect rect;
            if (!IsWindowVisible(window) || IsIconic(window) || !GetClientRect(window, out rect)) throw Failure("SC_WINDOW_UNAVAILABLE");
            CheckSize(rect.Right, rect.Bottom);
            using (var bitmap = new Bitmap(rect.Right, rect.Bottom, PixelFormat.Format24bppRgb))
            {
                var captured = DateTime.UtcNow;
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Black);
                    var dc = graphics.GetHdc();
                    try
                    {
                        // ponytail: documented PrintWindow client capture only; no desktop pixels or input-based fallback.
                        if (!PrintWindow(window, dc, 1)) throw Failure("SC_CAPTURE_FAILED");
                    }
                    finally { graphics.ReleaseHdc(dc); }
                }
                Rect after;
                if (!IsWindowVisible(window) || IsIconic(window) || !GetClientRect(window, out after) ||
                    after.Right != rect.Right || after.Bottom != rect.Bottom) throw Failure("SC_WINDOW_CHANGED");
                CheckNotBlank(bitmap);
                using (var memory = new MemoryStream())
                {
                    bitmap.Save(memory, ImageFormat.Png);
                    if (memory.Length > MaxPngBytes) throw Failure("SC_SIZE");
                    return new PowerSiFrame { Png = memory.ToArray(), CapturedUtc = captured, PixelSize = bitmap.Size };
                }
            }
        }

        private static void CheckSize(long width, long height)
        {
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096 || width * height > 16 * 1024 * 1024)
                throw Failure("SC_SIZE");
        }

        private static void CheckNotBlank(Bitmap bitmap)
        {
            var bits = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var row = new byte[bitmap.Width * 3]; int blue = -1, green = -1, red = -1;
                for (var y = 0; y < bitmap.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), row, 0, row.Length);
                    if (y == 0) { blue = row[0]; green = row[1]; red = row[2]; }
                    for (var x = 0; x < row.Length; x += 3)
                        if (row[x] != blue || row[x + 1] != green || row[x + 2] != red) return;
                }
                throw Failure("SC_BLANK"); // Nonuniform pixels do not prove that an application rendered every pane correctly.
            }
            finally { bitmap.UnlockBits(bits); }
        }

        internal static PowerSiFrame Parse(string wire)
        {
            if (Errors.Contains(wire)) throw Failure(wire);
            if (wire == null || wire.Length > MaxWireChars) throw Failure("SC_INVALID_IMAGE");
            var parts = wire.Split('|'); long ticks;
            if (parts.Length != 3 || parts[0] != "SC1" || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out ticks) ||
                ticks < 1 || ticks > DateTime.MaxValue.Ticks || ticks.ToString(CultureInfo.InvariantCulture) != parts[1]) throw Failure("SC_INVALID_IMAGE");
            byte[] png;
            try { png = Convert.FromBase64String(parts[2]); }
            catch (FormatException) { throw Failure("SC_INVALID_IMAGE"); }
            if (png.Length < 33 || png.Length > MaxPngBytes || Convert.ToBase64String(png) != parts[2] ||
                !png.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                !png.Skip(8).Take(8).SequenceEqual(new byte[] { 0, 0, 0, 13, 73, 72, 68, 82 })) throw Failure("SC_INVALID_IMAGE");
            long width = 0, height = 0;
            for (var i = 16; i < 20; i++) { width = (width << 8) + png[i]; height = (height << 8) + png[i + 4]; }
            CheckSize(width, height); // Validate compressed-image dimensions before asking GDI+ to decode it.
            try
            {
                using (var memory = new MemoryStream(png, false))
                using (var image = Image.FromStream(memory, false, true))
                    if (image.Width != width || image.Height != height) throw Failure("SC_INVALID_IMAGE");
            }
            catch { throw Failure("SC_INVALID_IMAGE"); }
            return new PowerSiFrame { Png = png, CapturedUtc = new DateTime(ticks, DateTimeKind.Utc), PixelSize = new Size((int)width, (int)height) };
        }

        internal static byte[] Crop(PowerSiFrame frame, Rectangle bounds)
        {
            // Coordinates only select existing pixels. Never recapture, resize, synthesize pixels, or operate PowerSI.
            if (frame == null || bounds.X < 0 || bounds.Y < 0 || bounds.Width <= 0 || bounds.Height <= 0 ||
                bounds.Width > frame.PixelSize.Width || bounds.Height > frame.PixelSize.Height ||
                bounds.X > frame.PixelSize.Width - bounds.Width || bounds.Y > frame.PixelSize.Height - bounds.Height ||
                bounds.Size == frame.PixelSize) throw new LocalVisionException("REGION_INVALID");
            using (var stream = new MemoryStream(frame.Png, false))
            using (var source = new Bitmap(stream))
            {
                if (source.Size != frame.PixelSize) throw new LocalVisionException("IMAGE_INVALID");
                using (var crop = source.Clone(bounds, PixelFormat.Format24bppRgb))
                using (var output = new MemoryStream())
                {
                    crop.Save(output, ImageFormat.Png);
                    if (output.Length > MaxPngBytes) throw new LocalVisionException("IMAGE_INVALID");
                    return output.ToArray();
                }
            }
        }

        internal static void SelfTest()
        {
            void Reject(Action action, string code)
            {
                try { action(); }
                catch (InvalidDataException error) when (error.Message == code) { return; }
                throw new InvalidOperationException("PowerSI capture self-test: " + code);
            }
            CheckSize(4096, 4096);
            Reject(() => CheckSize(4097, 1), "SC_SIZE");
            Reject(() => CheckSize(0, 50), "SC_SIZE");
            Reject(() => Parse("SC1|1|AAAA"), "SC_INVALID_IMAGE");
            Reject(() => Parse("SC_TIMEOUT"), "SC_TIMEOUT");
            Reject(() => Parse(new string('X', MaxWireChars + 1)), "SC_INVALID_IMAGE");
            using (var bitmap = new Bitmap(32, 24, PixelFormat.Format24bppRgb))
            {
                using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.White);
                Reject(() => CheckNotBlank(bitmap), "SC_BLANK");
                bitmap.SetPixel(8, 8, Color.Black);
                CheckNotBlank(bitmap);
                using (var memory = new MemoryStream())
                {
                    bitmap.Save(memory, ImageFormat.Png);
                    var png = memory.ToArray(); var now = DateTime.UtcNow;
                    var decoded = Parse("SC1|" + now.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + Convert.ToBase64String(png));
                    if (decoded.CapturedUtc != now || !decoded.Png.SequenceEqual(png)) throw new InvalidOperationException("PowerSI capture roundtrip.");
                    var region = new Rectangle(4, 5, 10, 8);
                    using (var cropped = new MemoryStream(Crop(decoded, region), false))
                    using (var image = new Bitmap(cropped))
                    {
                        if (image.Size != region.Size) throw new InvalidOperationException("Crop dimensions changed.");
                        for (int y = 0; y < region.Height; y++)
                            for (int x = 0; x < region.Width; x++)
                                if (image.GetPixel(x, y) != bitmap.GetPixel(x + region.X, y + region.Y))
                                    throw new InvalidOperationException("Crop must preserve exact source pixels.");
                    }
                    foreach (var invalid in new[] { new Rectangle(-1, 0, 10, 8), new Rectangle(0, 0, 0, 8),
                        new Rectangle(0, 0, 32, 24), new Rectangle(31, 23, 2, 2), new Rectangle(int.MaxValue, 0, 8, 8) })
                    {
                        try { Crop(decoded, invalid); throw new InvalidOperationException("Invalid crop accepted."); }
                        catch (LocalVisionException ex) when (ex.Code == "REGION_INVALID") { }
                    }
                    png[16] = 127;
                    Reject(() => Parse("SC1|1|" + Convert.ToBase64String(png)), "SC_SIZE");
                }
            }
        }

        private static InvalidDataException Failure(string code) { return new InvalidDataException(code); }
        [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
        private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);
        [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int size, out uint needed);
        [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("wtsapi32.dll")] private static extern bool WTSQuerySessionInformation(IntPtr server, int session, int info, out IntPtr buffer, out int size);
        [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    }
}
