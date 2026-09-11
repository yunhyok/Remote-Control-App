using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RemoteMonitorLink;

namespace RemoteMonitorSlave
{
    // API: the only writing input path in this application. Everything else stays read-only.
    //
    // OutputAnchor
    //   internal sealed class with fields Pid, StartUtcTicks, SessionId, ClientPoint, ClientSize, LearnedUtc.
    //   string Serialize()                      -> "A1|pid|ticks|session|x|y|w|h|learnedTicks" (uppercase/digits/pipes only).
    //   static bool TryParse(string, out OutputAnchor)
    //
    // OutputAutoCopy
    //   const string WorkerArgument             = "--powersi-output-auto-copy".
    //   argv of the worker: --powersi-output-auto-copy <sessionId> <pid:startTicks,...> <baselineSeq> <A1|...>
    //   string LearnAnchor(IntPtr root, ProcessInventory inventory)
    //       Sampled by the existing --powersi-output-copy worker right after TryReadUserCopy succeeded.
    //       Returns "A1|..." or "ANCHOR_NONE|<REASON>" (FOREGROUND, IDENTITY, CURSOR, CLIENT, OUTSIDE, SERIALIZE, SAMPLE).
    //       The copy worker appends it to the OB1 detail as "SOURCE_PID_MATCH|" + this value.
    //   OutputAnchor AnchorOf(OutputBufferResult result)
    //       Reads that suffix back on the UI side (null when the worker learned nothing). The anchor is only a hint;
    //       phase 2 must confirm it with OutputPaneImage.FindBodyAt on the run's frame before storing it.
    //   OutputBufferResult RunWorker(string[] args)
    //       Worker body, dispatched from OutputBufferCapture.TryRunWorker. Never throws for known failures; returns
    //       Code=AUTO_COPY_* / Method=NONE / Detail=<metadata or NONE>. Success: Code=AUTO_COPY_READ,
    //       Method=AUTO_CLIPBOARD, Detail="A2|anchorX|anchorY|bodyX|bodyY|bodyW|bodyH|clickScreenX|clickScreenY|B2|...".
    //       clickX/clickY are physical SCREEN coordinates of the injected click; the body rectangle is client-relative.
    //   Task<OutputBufferResult> AutoCopyAsync(ProcessInventory, OutputAnchor, uint baseline, CancellationToken)
    //       UI-side helper. Spawns the worker process (8 s budget) so a hang cannot freeze the UI, and parses OB1.
    //   void SelfTest()                         Called from OutputBufferCapture.SelfTest(); Program.cs needs no change.
    //
    // Behaviour notes for phase 2:
    //   * The clipboard is NOT restored after an auto copy (the copied Output text is the result we hand to the caller).
    //   * The cursor position is saved before the click and restored best effort at the end of the worker, success or not.
    //   * Exactly one click, one Ctrl+A and one Ctrl+C are injected; every key is released in a finally block.
    //   * Every stored coordinate is re-verified live (window identity, client size, body search, hit test, foreground).
    //   * The live part of SelfTest() replaces the current clipboard content with its own sample text and does not save
    //     or restore the previous content; it is skipped when no interactive foreground window can be obtained.
    internal sealed class OutputAnchor
    {
        internal const int MaxClientSide = 4096;

        internal int Pid;
        internal long StartUtcTicks;
        internal int SessionId;
        internal Point ClientPoint;
        internal Size ClientSize;
        internal DateTime LearnedUtc;

        internal string Serialize()
        {
            return string.Join("|", "A1", Text(Pid), Text(StartUtcTicks), Text(SessionId), Text(ClientPoint.X),
                Text(ClientPoint.Y), Text(ClientSize.Width), Text(ClientSize.Height), Text(LearnedUtc.Ticks));
        }

        internal static bool TryParse(string text, out OutputAnchor anchor)
        {
            anchor = null;
            if (text == null || text.Length > 160) return false;
            var parts = text.Split('|');
            if (parts.Length != 9 || parts[0] != "A1") return false;
            int pid, session, x, y, width, height;
            long start, learned;
            if (!Number(parts[1], out pid) || !Number(parts[3], out session) || !Number(parts[4], out x) ||
                !Number(parts[5], out y) || !Number(parts[6], out width) || !Number(parts[7], out height) ||
                !Number(parts[2], out start) || !Number(parts[8], out learned)) return false;
            if (pid < 1 || start < 1 || start > DateTime.MaxValue.Ticks || learned < 1 || learned > DateTime.MaxValue.Ticks ||
                width < 1 || height < 1 || width > MaxClientSide || height > MaxClientSide ||
                x < 0 || y < 0 || x >= width || y >= height) return false;
            anchor = new OutputAnchor { Pid = pid, StartUtcTicks = start, SessionId = session,
                ClientPoint = new Point(x, y), ClientSize = new Size(width, height),
                LearnedUtc = new DateTime(learned, DateTimeKind.Utc) };
            return true;
        }

        internal bool Matches(OutputAnchor other)
        {
            return other != null && other.Pid == Pid && other.StartUtcTicks == StartUtcTicks && other.SessionId == SessionId &&
                other.ClientPoint == ClientPoint && other.ClientSize == ClientSize && other.LearnedUtc == LearnedUtc;
        }

        private static string Text(long value) { return value.ToString(CultureInfo.InvariantCulture); }

        private static bool Number(string text, out int value)
        {
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
                value.ToString(CultureInfo.InvariantCulture) == text;
        }

        private static bool Number(string text, out long value)
        {
            return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
                value.ToString(CultureInfo.InvariantCulture) == text;
        }
    }

    internal static class OutputAutoCopy
    {
        internal const string WorkerArgument = "--powersi-output-auto-copy";
        internal const int BudgetMilliseconds = 8000;
        private const int ClickHoldMilliseconds = 60;
        private const int ForegroundWaitMilliseconds = 600;
        private const int ChordGapMilliseconds = 80;
        private const int ClipboardWaitMilliseconds = 2000;

        private const uint InputMouse = 0, InputKeyboard = 1;
        private const uint MouseLeftDown = 0x0002, MouseLeftUp = 0x0004, MouseRightDown = 0x0008, MouseRightUp = 0x0010;
        private const uint KeyScanCode = 0x0008, KeyUp = 0x0002;
        private const ushort VirtualControl = 0x11, VirtualA = 0x41, VirtualC = 0x43;
        private const uint RootAncestor = 2; // GA_ROOT
        private const int SwapButtonMetric = 23; // SM_SWAPBUTTON
        // Physical mouse buttons and every modifier that could turn our click or chord into a different command.
        private static readonly int[] HeldKeys = { 0x01, 0x02, 0x04, 0x05, 0x06, 0x10, 0x11, 0x12, 0x5B, 0x5C };

        // ---------------------------------------------------------------- anchor learning (read-only sampling)

        internal static string LearnAnchor(IntPtr root, ProcessInventory inventory)
        {
            try
            {
                if (root == IntPtr.Zero || inventory == null || inventory.Items == null) return "ANCHOR_NONE|IDENTITY";
                var foreground = GetForegroundWindow();
                if (foreground == IntPtr.Zero || (foreground != root && RootOf(foreground) != root)) return "ANCHOR_NONE|FOREGROUND";
                uint pid;
                if (GetWindowThreadProcessId(root, out pid) == 0 || pid == 0 || pid > int.MaxValue) return "ANCHOR_NONE|IDENTITY";
                var identity = inventory.Items.SingleOrDefault(p => p.Pid == (int)pid);
                if (identity == null || !identity.StartUtcTicks.HasValue || identity.StartUtcTicks.Value < 1) return "ANCHOR_NONE|IDENTITY";
                NativePoint cursor;
                if (!GetPhysicalCursorPos(out cursor) || !ScreenToClient(root, ref cursor)) return "ANCHOR_NONE|CURSOR";
                NativeRect client;
                if (!GetClientRect(root, out client)) return "ANCHOR_NONE|CLIENT";
                if (client.Right < 1 || client.Bottom < 1 || client.Right > OutputAnchor.MaxClientSide ||
                    client.Bottom > OutputAnchor.MaxClientSide) return "ANCHOR_NONE|CLIENT";
                if (cursor.X < 0 || cursor.Y < 0 || cursor.X >= client.Right || cursor.Y >= client.Bottom) return "ANCHOR_NONE|OUTSIDE";
                var anchor = new OutputAnchor { Pid = (int)pid, StartUtcTicks = identity.StartUtcTicks.Value,
                    SessionId = inventory.SessionId, ClientPoint = new Point(cursor.X, cursor.Y),
                    ClientSize = new Size(client.Right, client.Bottom), LearnedUtc = DateTime.UtcNow };
                var text = anchor.Serialize();
                OutputAnchor parsed;
                return OutputAnchor.TryParse(text, out parsed) && parsed.Matches(anchor) ? text : "ANCHOR_NONE|SERIALIZE";
            }
            catch { return "ANCHOR_NONE|SAMPLE"; }
        }

        internal static OutputAnchor AnchorOf(OutputBufferResult result)
        {
            if (result == null || result.Detail == null) return null;
            int separator = result.Detail.IndexOf('|');
            if (separator < 0) return null;
            OutputAnchor anchor;
            return OutputAnchor.TryParse(result.Detail.Substring(separator + 1), out anchor) ? anchor : null;
        }

        // ---------------------------------------------------------------- UI side

        internal static Task<OutputBufferResult> AutoCopyAsync(ProcessInventory inventory, OutputAnchor anchor,
            uint baseline, CancellationToken cancellation)
        {
            string arguments;
            try
            {
                if (inventory == null || anchor == null) return Task.FromResult(Result("AUTO_COPY_REQUEST_INVALID", null));
                inventory.Validate();
                if (inventory.Items.Length == 0 || inventory.Omitted != 0 ||
                    inventory.Items.Any(p => !ProcessInventory.IsPowerSiName(p.Name) || !p.StartUtcTicks.HasValue))
                    return Task.FromResult(Result("AUTO_COPY_REQUEST_INVALID", null));
                OutputAnchor parsed;
                var serialized = anchor.Serialize();
                if (!OutputAnchor.TryParse(serialized, out parsed) || !parsed.Matches(anchor))
                    return Task.FromResult(Result("AUTO_COPY_REQUEST_INVALID", null));
                if (inventory.SessionId != anchor.SessionId ||
                    !inventory.Items.Any(p => p.Pid == anchor.Pid && p.StartUtcTicks.Value == anchor.StartUtcTicks))
                    return Task.FromResult(Result("AUTO_COPY_ANCHOR_STALE", null));
                var identities = string.Join(",", inventory.Items.Select(p => p.Pid.ToString(CultureInfo.InvariantCulture) + ":" +
                    p.StartUtcTicks.Value.ToString(CultureInfo.InvariantCulture)));
                arguments = WorkerArgument + " " + inventory.SessionId.ToString(CultureInfo.InvariantCulture) + " " + identities +
                    " " + baseline.ToString(CultureInfo.InvariantCulture) + " " + serialized;
            }
            catch (OperationCanceledException) { throw; }
            catch { return Task.FromResult(Result("AUTO_COPY_REQUEST_INVALID", null)); }
            return OutputBufferCapture.RunWorkerAsync(arguments, BudgetMilliseconds, "AUTO_COPY_TIMEOUT",
                "AUTO_COPY_WORKER_FAILED", cancellation);
        }

        // ---------------------------------------------------------------- worker

        internal static OutputBufferResult RunWorker(string[] args)
        {
            try
            {
                uint baseline;
                OutputAnchor anchor;
                if (args == null || args.Length != 5 || args[0] != WorkerArgument ||
                    !uint.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out baseline) ||
                    !OutputAnchor.TryParse(args[4], out anchor)) throw Failure("AUTO_COPY_REQUEST_INVALID", null);
                return Run(args, anchor, baseline);
            }
            catch (AutoCopyException error) { return Result(error.Message, error.Detail); }
            catch (InvalidDataException error) { return Result(error.Message, null); }
            catch { return Result("AUTO_COPY_FAILED", null); }
        }

        private static OutputBufferResult Run(string[] args, OutputAnchor anchor, uint baseline)
        {
            var targetArgs = new[] { args[0], args[1], args[2] };
            var inventory = OutputBufferCapture.ParseInventory(args[1], args[2]);
            var root = PowerSiScreenCapture.ResolveWindow(targetArgs); // Fresh HWND plus full PowerSI identity checks.
            CheckTarget(root, inventory, anchor);

            BodySearchDiagnostics diagnostics;
            Rectangle body;
            var frame = CaptureFrame(root);
            try { body = OutputPaneImage.FindBodyAt(frame, anchor.ClientPoint, CancellationToken.None, out diagnostics); }
            catch (LocalVisionException error) { throw Failure("AUTO_COPY_BODY_UNCONFIRMED", error.Detail); }
            var summary = diagnostics == null ? "NONE" : Sanitize(diagnostics.Summary());
            if (body.Width < 1 || body.Height < 1 || !body.Contains(anchor.ClientPoint) ||
                body.Right > anchor.ClientSize.Width || body.Bottom > anchor.ClientSize.Height || body.X < 0 || body.Y < 0)
                throw Failure("AUTO_COPY_BODY_UNCONFIRMED", summary);

            var target = new NativePoint { X = body.X + body.Width / 2, Y = body.Y + body.Height / 2 };
            if (!ClientToScreen(root, ref target)) throw Failure("AUTO_COPY_WINDOW_CHANGED", null);
            RequireIdleInput();
            if (RootOf(WindowFromPhysicalPoint(target)) != root) throw Failure("AUTO_COPY_OCCLUDED", null);
            var swapped = GetSystemMetrics(SwapButtonMetric) != 0;

            NativePoint saved;
            var savedCursor = GetPhysicalCursorPos(out saved);
            OutputBufferResult copied;
            try
            {
                if (!SetPhysicalCursorPos(target.X, target.Y)) throw Failure("AUTO_COPY_CURSOR_NOT_SET", null);
                NativePoint current;
                if (!GetPhysicalCursorPos(out current) || current.X != target.X || current.Y != target.Y)
                    throw Failure("AUTO_COPY_CURSOR_NOT_SET", null);
                if (RootOf(WindowFromPhysicalPoint(current)) != root) throw Failure("AUTO_COPY_OCCLUDED", null);
                if ((GetSystemMetrics(SwapButtonMetric) != 0) != swapped) throw Failure("AUTO_COPY_INPUT_BUSY", null);
                ClickOnce(swapped);
                WaitForeground(root);
                if (!GetPhysicalCursorPos(out current) || current.X != target.X || current.Y != target.Y)
                    throw Failure("AUTO_COPY_CURSOR_MOVED", null);
                if (RootOf(WindowFromPhysicalPoint(current)) != root) throw Failure("AUTO_COPY_OCCLUDED", null);
                Chord(root, VirtualA);
                Thread.Sleep(ChordGapMilliseconds);
                Chord(root, VirtualC);
                copied = ReadCopiedText(inventory, baseline);
            }
            finally { if (savedCursor) { try { SetPhysicalCursorPos(saved.X, saved.Y); } catch { } } }

            if (PowerSiScreenCapture.ResolveWindow(targetArgs) != root) throw Failure("AUTO_COPY_WINDOW_CHANGED", null);
            copied.Code = "AUTO_COPY_READ";
            copied.Method = "AUTO_CLIPBOARD";
            copied.Detail = Sanitize(string.Join("|", "A2", Text(anchor.ClientPoint.X), Text(anchor.ClientPoint.Y),
                Text(body.X), Text(body.Y), Text(body.Width), Text(body.Height), Text(target.X), Text(target.Y), summary));
            return copied;
        }

        private static void CheckTarget(IntPtr root, ProcessInventory inventory, OutputAnchor anchor)
        {
            uint pid;
            if (GetWindowThreadProcessId(root, out pid) == 0 || pid == 0 || (int)pid != anchor.Pid)
                throw Failure("AUTO_COPY_WINDOW_CHANGED", null);
            if (inventory.SessionId != anchor.SessionId) throw Failure("AUTO_COPY_WINDOW_CHANGED", null);
            var identity = inventory.Items.SingleOrDefault(p => p.Pid == anchor.Pid);
            if (identity == null || !identity.StartUtcTicks.HasValue || identity.StartUtcTicks.Value != anchor.StartUtcTicks)
                throw Failure("AUTO_COPY_WINDOW_CHANGED", null);
            if (!IsWindowVisible(root) || IsIconic(root)) throw Failure("AUTO_COPY_WINDOW_CHANGED", null);
            NativeRect client;
            if (!GetClientRect(root, out client) || client.Right != anchor.ClientSize.Width ||
                client.Bottom != anchor.ClientSize.Height) throw Failure("AUTO_COPY_WINDOW_CHANGED", null);
        }

        // The screenshot worker owns the capture rules (visibility, blank and size checks); reuse it in this process
        // instead of spawning a second worker. SelfTest() verifies that the reused method is still present.
        private static readonly MethodInfo CaptureWindowMethod = typeof(PowerSiScreenCapture).GetMethod("CaptureWindow",
            BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(IntPtr) }, null);

        private static PowerSiFrame CaptureFrame(IntPtr window)
        {
            if (CaptureWindowMethod == null || CaptureWindowMethod.ReturnType != typeof(PowerSiFrame))
                throw Failure("AUTO_COPY_CAPTURE_UNAVAILABLE", null);
            object frame;
            try { frame = CaptureWindowMethod.Invoke(null, new object[] { window }); }
            catch (TargetInvocationException error)
            {
                var inner = error.InnerException as InvalidDataException;
                throw Failure("AUTO_COPY_CAPTURE_FAILED", inner == null ? null : inner.Message);
            }
            catch { throw Failure("AUTO_COPY_CAPTURE_FAILED", null); }
            var captured = frame as PowerSiFrame;
            if (captured == null || captured.Png == null) throw Failure("AUTO_COPY_CAPTURE_FAILED", null);
            return captured;
        }

        private static OutputBufferResult ReadCopiedText(ProcessInventory inventory, uint baseline)
        {
            var clock = Stopwatch.StartNew();
            while (OutputBufferCapture.ClipboardSequence == baseline)
            {
                if (clock.ElapsedMilliseconds >= ClipboardWaitMilliseconds) throw Failure("AUTO_COPY_NO_CLIPBOARD", null);
                Thread.Sleep(50);
            }
            // The copying application may still hold the clipboard open for a moment; a few read-only retries only.
            for (var attempt = 0; attempt < 4; attempt++)
            {
                OutputBufferResult result;
                if (OutputBufferCapture.TryReadUserCopy(inventory, baseline, out result) && result != null)
                {
                    if (result.Text == null) throw Failure("AUTO_COPY_CLIPBOARD_SIZE", Sanitize(result.Code));
                    return result;
                }
                Thread.Sleep(50);
            }
            throw Failure("AUTO_COPY_CLIPBOARD_FOREIGN", null);
        }

        // ---------------------------------------------------------------- injection primitives

        private static void RequireIdleInput()
        {
            foreach (var key in HeldKeys)
                if (GetAsyncKeyState(key) < 0) throw Failure("AUTO_COPY_INPUT_BUSY", null); // High bit: physically held.
        }

        private static void WaitForeground(IntPtr root)
        {
            var clock = Stopwatch.StartNew();
            while (RootOf(GetForegroundWindow()) != root)
            {
                if (clock.ElapsedMilliseconds >= ForegroundWaitMilliseconds) throw Failure("AUTO_COPY_FOREGROUND_FAILED", null);
                Thread.Sleep(20);
            }
        }

        // Exactly one press and one release; the release is attempted in the finally even when the hold fails.
        private static void ClickOnce(bool swapped)
        {
            var down = new[] { Mouse(swapped ? MouseRightDown : MouseLeftDown) };
            var up = new[] { Mouse(swapped ? MouseRightUp : MouseLeftUp) };
            var pressed = false;
            var released = -1;
            try
            {
                if (Send(down) != 1) throw Failure("AUTO_COPY_CLICK_FAILED", null);
                pressed = true;
                Thread.Sleep(ClickHoldMilliseconds);
            }
            finally
            {
                if (pressed) { try { released = (int)Send(up); } catch { released = -1; } }
            }
            if (released != 1) throw Failure("AUTO_COPY_CLICK_FAILED", null);
        }

        private static void Chord(IntPtr root, ushort key)
        {
            if (RootOf(GetForegroundWindow()) != root) throw Failure("AUTO_COPY_FOREGROUND_LOST", null);
            var packets = ComposeChord(key);
            var returned = false;
            var inserted = 0;
            try
            {
                inserted = (int)Send(packets);
                returned = true;
                if (inserted != packets.Length) throw Failure("AUTO_COPY_KEYS_FAILED", null);
            }
            finally
            {
                // packets are [CTRL down, key down, key up, CTRL up]; anything not inserted is still logically held.
                if (!returned || inserted == 2) { try { Send(new[] { Key(key, true) }); } catch { } }
                if (!returned || (inserted >= 1 && inserted <= 3)) { try { Send(new[] { Key(VirtualControl, true) }); } catch { } }
            }
        }

        internal static Input[] ComposeChord(ushort key)
        {
            return new[] { Key(VirtualControl, false), Key(key, false), Key(key, true), Key(VirtualControl, true) };
        }

        private static Input Mouse(uint flags)
        {
            var input = new Input { Type = InputMouse };
            input.Union.Mouse.Flags = flags;
            return input;
        }

        private static Input Key(ushort virtualKey, bool up)
        {
            var scan = (ushort)MapVirtualKey(virtualKey, 0); // MAPVK_VK_TO_VSC
            if (scan == 0) throw Failure("AUTO_COPY_KEYS_UNAVAILABLE", null);
            var input = new Input { Type = InputKeyboard };
            input.Union.Keyboard.Scan = scan; // Scan codes only; wVk is ignored when KEYEVENTF_SCANCODE is set.
            input.Union.Keyboard.Flags = up ? (KeyScanCode | KeyUp) : KeyScanCode;
            return input;
        }

        private static uint Send(Input[] packets)
        {
            var size = Marshal.SizeOf(typeof(Input));
            if (size != (IntPtr.Size == 8 ? 40 : 28)) throw Failure("AUTO_COPY_INPUT_LAYOUT_INVALID", null);
            return SendInput((uint)packets.Length, packets, size);
        }

        private static IntPtr RootOf(IntPtr window)
        {
            return window == IntPtr.Zero ? IntPtr.Zero : GetAncestor(window, RootAncestor);
        }

        // ---------------------------------------------------------------- results

        private sealed class AutoCopyException : InvalidDataException
        {
            internal readonly string Detail;
            internal AutoCopyException(string code, string detail) : base(code) { Detail = detail; }
        }

        private static AutoCopyException Failure(string code, string detail) { return new AutoCopyException(code, detail); }

        private static OutputBufferResult Result(string code, string detail)
        {
            return new OutputBufferResult { Code = OutputBufferCapture.SafeCode(code) ? code : "AUTO_COPY_FAILED",
                Method = "NONE", Detail = Sanitize(detail) };
        }

        private static string Sanitize(string detail)
        {
            return detail != null && Regex.IsMatch(detail, @"\A[A-Z0-9_:=,| -]{1,400}\z") ? detail : "NONE";
        }

        private static string Text(int value) { return value.ToString(CultureInfo.InvariantCulture); }

        // ---------------------------------------------------------------- self-test

        internal static void SelfTest()
        {
            AnchorSelfTest();
            DetailSelfTest();
            LayoutSelfTest();
            Console.WriteLine(LiveTest());
        }

        private static void Need(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Output auto copy self-test: " + name);
        }

        private static void AnchorSelfTest()
        {
            var learned = new DateTime(2026, 9, 11, 1, 2, 3, DateTimeKind.Utc);
            var anchor = new OutputAnchor { Pid = 4321, StartUtcTicks = 638000000000000000L, SessionId = 2,
                ClientPoint = new Point(320, 385), ClientSize = new Size(1920, 1040), LearnedUtc = learned };
            var text = anchor.Serialize();
            Need(text == "A1|4321|638000000000000000|2|320|385|1920|1040|" +
                learned.Ticks.ToString(CultureInfo.InvariantCulture), "anchor serialization format");
            OutputAnchor parsed;
            Need(OutputAnchor.TryParse(text, out parsed) && parsed.Matches(anchor) && parsed.Serialize() == text,
                "anchor round trip");
            Need(parsed.LearnedUtc.Kind == DateTimeKind.Utc, "anchor learned time stays UTC");
            foreach (var invalid in new[] { null, "", "A1", "A2|4321|1|2|320|385|1920|1040|1", "A1|4321|1|2|320|385|1920|1040",
                "A1|4321|1|2|320|385|1920|1040|1|9", "A1|0|1|2|320|385|1920|1040|1", "A1|4321|0|2|320|385|1920|1040|1",
                "A1|4321|1|2|1920|385|1920|1040|1", "A1|4321|1|2|320|1040|1920|1040|1", "A1|4321|1|2|-1|385|1920|1040|1",
                "A1|4321|1|-2|320|385|1920|1040|1", "A1|4321|1|2|320|385|4097|1040|1", "A1|4321|1|2|320|385|1920|0|1",
                "A1|4321|1|2|320|385|1920|1040|0", "A1|+4321|1|2|320|385|1920|1040|1", "A1|4321|1|2|320|385|1920|1040| 1",
                "A1|4321|1|2|320|385|1920|1040|01", "a1|4321|1|2|320|385|1920|1040|1", "A1|4321|99999999999999999999|2|320|385|1920|1040|1" })
            {
                OutputAnchor rejected;
                Need(!OutputAnchor.TryParse(invalid, out rejected) && rejected == null, "malformed anchor rejected: " + (invalid ?? "null"));
            }
            Need(Sanitize(text) == text, "anchor text survives the OB1 detail character set");
        }

        private static void DetailSelfTest()
        {
            var anchor = new OutputAnchor { Pid = 7, StartUtcTicks = 123456789, SessionId = 1,
                ClientPoint = new Point(10, 20), ClientSize = new Size(800, 600), LearnedUtc = DateTime.UtcNow };
            var carrier = new OutputBufferResult { Code = "USER_COPY_READ", Method = "USER_CLIPBOARD",
                Detail = "SOURCE_PID_MATCH|" + anchor.Serialize() };
            var read = AnchorOf(carrier);
            Need(read != null && read.Pid == 7 && read.ClientPoint == new Point(10, 20) && read.ClientSize == new Size(800, 600),
                "anchor read back from the OB1 detail");
            Need(carrier.Detail.Split('|')[0] == "SOURCE_PID_MATCH", "legacy detail prefix preserved");
            Need(AnchorOf(new OutputBufferResult { Detail = "SOURCE_PID_MATCH" }) == null, "detail without anchor");
            Need(AnchorOf(new OutputBufferResult { Detail = "SOURCE_PID_MATCH|ANCHOR_NONE|CURSOR" }) == null, "anchor-none detail");
            Need(AnchorOf(new OutputBufferResult { Detail = "SOURCE_PID_MATCH|A1|7|123456789|1|10|20|800" }) == null, "truncated anchor detail");
            Need(AnchorOf(null) == null && AnchorOf(new OutputBufferResult()) == null, "missing detail");
            foreach (var reason in new[] { "FOREGROUND", "IDENTITY", "CURSOR", "CLIENT", "OUTSIDE", "SERIALIZE", "SAMPLE" })
                Need(Sanitize("SOURCE_PID_MATCH|ANCHOR_NONE|" + reason) != "NONE", "anchor-none reason is log safe: " + reason);
            Need(LearnAnchor(IntPtr.Zero, null) == "ANCHOR_NONE|IDENTITY", "anchor sampling without a window");
            var summary = "A2|320|385|20|40|360|320|1000|900|B2|1920|1040|12|1|3|4|1|2|1";
            Need(Sanitize(summary) == summary, "success detail is log safe");
            Need(Result("AUTO_COPY_BODY_UNCONFIRMED", null).Detail == "NONE" &&
                Result("auto_copy", null).Code == "AUTO_COPY_FAILED" &&
                Result("AUTO_COPY_BODY_UNCONFIRMED", "b2|1").Detail == "NONE", "failure results stay inside the wire contract");
        }

        private static void LayoutSelfTest()
        {
            var size = Marshal.SizeOf(typeof(Input));
            Need(size == (IntPtr.Size == 8 ? 40 : 28) && Marshal.OffsetOf(typeof(Input), "Union").ToInt32() == (IntPtr.Size == 8 ? 8 : 4) &&
                Marshal.SizeOf(typeof(InputUnion)) == (IntPtr.Size == 8 ? 32 : 24) &&
                Marshal.SizeOf(typeof(MouseInput)) == (IntPtr.Size == 8 ? 32 : 24) &&
                Marshal.SizeOf(typeof(KeyboardInput)) == (IntPtr.Size == 8 ? 24 : 16), "INPUT union layout");
            Need(CaptureWindowMethod != null && CaptureWindowMethod.ReturnType == typeof(PowerSiFrame),
                "reused in-process window capture is still available");
            foreach (var key in new[] { VirtualA, VirtualC })
            {
                var packets = ComposeChord(key);
                var control = (ushort)MapVirtualKey(VirtualControl, 0);
                var scan = (ushort)MapVirtualKey(key, 0);
                Need(packets.Length == 4 && control != 0 && scan != 0, "chord packet count");
                Need(packets[0].Type == InputKeyboard && packets[1].Type == InputKeyboard &&
                    packets[2].Type == InputKeyboard && packets[3].Type == InputKeyboard, "chord uses keyboard input only");
                Need(packets[0].Union.Keyboard.Scan == control && packets[3].Union.Keyboard.Scan == control &&
                    packets[1].Union.Keyboard.Scan == scan && packets[2].Union.Keyboard.Scan == scan, "chord scan codes");
                Need(packets[0].Union.Keyboard.Flags == KeyScanCode && packets[1].Union.Keyboard.Flags == KeyScanCode &&
                    packets[2].Union.Keyboard.Flags == (KeyScanCode | KeyUp) && packets[3].Union.Keyboard.Flags == (KeyScanCode | KeyUp),
                    "chord press/release order");
                foreach (var packet in packets)
                    Need(packet.Union.Keyboard.Vk == 0 && packet.Union.Keyboard.Time == 0 &&
                        packet.Union.Keyboard.ExtraInfo == UIntPtr.Zero, "chord packets carry no injected metadata");
            }
            foreach (var swapped in new[] { false, true })
            {
                var down = Mouse(swapped ? MouseRightDown : MouseLeftDown);
                var up = Mouse(swapped ? MouseRightUp : MouseLeftUp);
                Need(down.Type == InputMouse && up.Type == InputMouse &&
                    down.Union.Mouse.Flags == (swapped ? MouseRightDown : MouseLeftDown) &&
                    up.Union.Mouse.Flags == (swapped ? MouseRightUp : MouseLeftUp) &&
                    down.Union.Mouse.X == 0 && down.Union.Mouse.Y == 0 && down.Union.Mouse.Data == 0 &&
                    down.Union.Mouse.Time == 0 && down.Union.Mouse.ExtraInfo == UIntPtr.Zero,
                    "click packets carry no movement or wheel data");
            }
        }

        // Live injection against our own window. It replaces the clipboard with the sample text below and never saves
        // or restores the previous clipboard content. Skipped whenever no interactive foreground window is available.
        private static string LiveTest()
        {
            const string Skip = "SKIP: live input test (no interactive foreground)";
            const string Sample = "AUTO COPY LIVE 1\r\nAUTO COPY LIVE 2";
            System.Windows.Forms.Form form = null;
            System.Windows.Forms.TextBox box = null;
            try
            {
                // Only the window setup may fail silently (a service session has no interactive desktop at all).
                // Once the first input packet is injected every check below is a hard assertion.
                try
                {
                    form = new System.Windows.Forms.Form
                    {
                        Text = "Remote Monitor self-test",
                        Width = 420,
                        Height = 240,
                        ShowInTaskbar = false,
                        TopMost = true,
                        StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen
                    };
                    box = new System.Windows.Forms.TextBox
                    {
                        Multiline = true,
                        Dock = System.Windows.Forms.DockStyle.Fill,
                        Text = Sample
                    };
                    form.Controls.Add(box);
                    form.Show();
                    form.Activate();
                }
                catch { return Skip; }
                if (box == null || !Pump(1000, () => GetForegroundWindow() == form.Handle)) return Skip;
                var target = new NativePoint { X = box.ClientSize.Width / 2, Y = box.ClientSize.Height / 2 };
                if (box.ClientSize.Width < 8 || box.ClientSize.Height < 8 || !ClientToScreen(box.Handle, ref target)) return Skip;
                NativePoint saved;
                var savedCursor = GetPhysicalCursorPos(out saved);
                try
                {
                    foreach (var key in HeldKeys) if (GetAsyncKeyState(key) < 0) return Skip;
                    if (!SetPhysicalCursorPos(target.X, target.Y)) return Skip;
                    NativePoint current;
                    if (!GetPhysicalCursorPos(out current) || current.X != target.X || current.Y != target.Y) return Skip;
                    if (RootOf(WindowFromPhysicalPoint(current)) != form.Handle) return Skip;
                    var baseline = OutputBufferCapture.ClipboardSequence;
                    ClickOnce(GetSystemMetrics(SwapButtonMetric) != 0);
                    Pump(ForegroundWaitMilliseconds, () => false);
                    Need(GetForegroundWindow() == form.Handle, "live test window stayed foreground");
                    Chord(form.Handle, VirtualA);
                    Pump(ChordGapMilliseconds, () => false);
                    Chord(form.Handle, VirtualC);
                    Need(Pump(ClipboardWaitMilliseconds, () => OutputBufferCapture.ClipboardSequence != baseline),
                        "live Ctrl+C changed the clipboard sequence");
                    string copied = null;
                    Pump(1000, () =>
                    {
                        try { copied = System.Windows.Forms.Clipboard.ContainsText() ? System.Windows.Forms.Clipboard.GetText() : null; }
                        catch { copied = null; }
                        return copied != null;
                    });
                    Need(copied == Sample, "live Ctrl+A/Ctrl+C copied the exact control text");
                }
                finally { if (savedCursor) { try { SetPhysicalCursorPos(saved.X, saved.Y); } catch { } } }
                return "PASS: live input test (click + Ctrl+A + Ctrl+C)";
            }
            finally
            {
                if (form != null) { try { form.Dispose(); } catch { } }
                try { System.Windows.Forms.Application.DoEvents(); } catch { }
            }
        }

        // The self-test owns the only message loop these controls get, so every wait has to pump it.
        private static bool Pump(int milliseconds, Func<bool> until)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                try { System.Windows.Forms.Application.DoEvents(); } catch { }
                if (until()) return true;
                if (clock.ElapsedMilliseconds >= milliseconds) return false;
                Thread.Sleep(20);
            }
        }

        // ---------------------------------------------------------------- native layer

        [StructLayout(LayoutKind.Sequential)]
        internal struct Input
        {
            internal uint Type;
            internal InputUnion Union;
        }

        // The union must be as large as its largest member: MOUSEINPUT, 32 bytes on x64 and 24 on x86
        // (KEYBDINPUT is 24/16). HARDWAREINPUT is never used and is therefore not declared.
        [StructLayout(LayoutKind.Explicit)]
        internal struct InputUnion
        {
            [FieldOffset(0)] internal MouseInput Mouse;
            [FieldOffset(0)] internal KeyboardInput Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MouseInput
        {
            internal int X, Y;
            internal uint Data, Flags, Time;
            internal UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct KeyboardInput
        {
            internal ushort Vk, Scan;
            internal uint Flags, Time;
            internal UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { internal int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { internal int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern IntPtr WindowFromPhysicalPoint(NativePoint point);
        // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setphysicalcursorpos (Vista+)
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetPhysicalCursorPos(int x, int y);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPhysicalCursorPos(out NativePoint point);
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "MapVirtualKeyW")]
        private static extern uint MapVirtualKey(uint code, uint type);
        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);
        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    }
}
