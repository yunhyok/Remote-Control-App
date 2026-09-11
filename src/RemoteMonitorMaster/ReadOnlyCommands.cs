using System;
using System.Linq;
using System.Threading;
using System.Text.RegularExpressions;
using RemoteMonitorLink;

namespace RemoteMonitorMaster
{
    // ponytail: fixed, case-sensitive phrases for the supervised test; no natural-language or shell parser.
    internal static class ReadOnlyCommands
    {
        private static readonly string[] Commands = { "help", "help help", "help total status", "help pwrsi", "total status", "pwrsi" };
        private static readonly string[] Hashes = Commands.Select(TokenStore.Hash).ToArray();

        internal static bool IsCommand(string command) { return Commands.Contains(command, StringComparer.Ordinal); }

        internal static bool TryMatchHash(string hash, out string command)
        {
            var index = Array.IndexOf(Hashes, hash);
            command = index < 0 ? null : Commands[index];
            return index >= 0;
        }

        internal static void ObserveName(ProbeNode node, string name)
        {
            node.PlainCommand = null;
            node.PlainCommandSourceHash = null;
            node.CommandNameFormat = "UNAVAILABLE";
            if (name == null || node.Identity == null || name.Length != node.Identity.NameLength ||
                TokenStore.Hash(name) != node.Identity.NameHash) return;
            node.CommandNameFormat = "UNSUPPORTED";
            if (name.Length > 64) { node.CommandNameFormat = "LONG_NAME"; return; }
            // ponytail: only outer space/NBSP is optional. Keep case, internal spacing, and raw identity strict.
            var trimmed = name.Trim(' ', '\u00a0');
            if (IsCommand(trimmed))
            {
                node.PlainCommand = trimmed;
                node.PlainCommandSourceHash = node.Identity.NameHash;
                node.CommandNameFormat = name == trimmed ? "EXACT" : "OUTER_SPACES";
            }
            else if (IsCommand(trimmed.ToLowerInvariant())) node.CommandNameFormat = "CASE_MISMATCH";
            else if (name != trimmed) node.CommandNameFormat = "UNSUPPORTED_EDGE_SPACES";
        }

        internal static bool TryMatchNode(ProbeNode node, out string command)
        {
            command = node == null ? null : node.PlainCommand;
            return IsCommand(command) && node.Identity != null && node.PlainCommandSourceHash == node.Identity.NameHash;
        }

        internal static string Capture(string command, string nonce, SlaveEndpoint endpoint, CancellationToken cancellation)
        {
            Need(IsCommand(command) && Protocol.IsDiagnosticMarker("DRAFT", nonce) && endpoint != null);
            cancellation.ThrowIfCancellationRequested();
            if (command.StartsWith("help", StringComparison.Ordinal)) return Help(command, nonce);
            var state = StatusClient.QueryAsync(endpoint, cancellation, command == "pwrsi").GetAwaiter().GetResult();
            cancellation.ThrowIfCancellationRequested();
            return FormatStatus(command, nonce, state);
        }

        private static string Help(string command, string nonce)
        {
            string body;
            switch (command)
            {
                case "help": body = "HELP | HELP HELP | HELP TOTAL STATUS | HELP PWRSI | TOTAL STATUS | PWRSI"; break;
                case "help help": body = "HELP: LIST ALL COMMANDS. HELP <COMMAND>: SHOW THAT COMMAND'S DESCRIPTION."; break;
                case "help total status": body = "TOTAL STATUS: SLAVE TIME, UPTIME, RAM, VERSION AND UP TO 8 PROCESSES WITH PID, CPU, RAM, AGE. CPU IS NOT SIMULATION PROGRESS."; break;
                case "help pwrsi": body = "PWRSI: SLAVE PID/CPU/RAM; LOCAL VISION MODEL READS OUTPUT/STATUS. IMAGE STAYS ON SLAVE. UP TO 115S. PERCENT UNAVAILABLE."; break;
                default: throw new MonitorException("COMMAND_INVALID", "Unknown read-only command.");
            }
            return "HELP " + nonce + " | " + body + " | TYPE LOWERCASE. PWRSI NEEDS NO SPACE; TOTAL STATUS HAS ONE BETWEEN WORDS. OUTER SPACES OPTIONAL. SEND ONE COMMAND AFTER EACH REPLY.";
        }

        internal static string FormatStatus(string command, string nonce, MachineStatus state)
        {
            Need(command == "total status" || command == "pwrsi");
            // Reuse the validated process report, then render every reply in uppercase: even split Text nodes cannot echo commands.
            var text = PcStatusReport.FormatSlave(nonce, state, command == "pwrsi" ? 4 : PcStatusReport.MaxPhoneProcesses).ToUpperInvariant();
            text = Regex.Replace(text, @"M([2-9]{6})", "M_$1", RegexOptions.CultureInvariant);
            if (command == "pwrsi")
            {
                Need(state.Processes.Items.All(p => ProcessInventory.IsPowerSiName(p.Name)));
                Need(state.PowerSi != null);
                text = "PWRSI STATUS" + text.Substring("SLAVE STATUS".Length);
                text += state.Processes.Items.Length == 0 ? " | NOT OBSERVED IN SLAVE SESSION (POWERSI/PWRSI ONLY)" : " | MATCH POWERSI/PWRSI ONLY";
                text += " | " + state.PowerSi.Summary;
            }
            Need(IsReply(text, command, nonce));
            return text;
        }

        // Exact prepared-payload comparison in Consent is authoritative; this checks final rendering and reply kind.
        internal static bool IsReply(string text, string command, string nonce)
        {
            if (!IsCommand(command) || !Protocol.IsDiagnosticMarker("DRAFT", nonce) || text == null ||
                text.Length > PcStatusReport.MaxPhoneLength || text.Any(char.IsControl) ||
                text != text.ToUpperInvariant() || Regex.IsMatch(text, @"M[2-9]{6}", RegexOptions.CultureInvariant)) return false;
            if (command.StartsWith("help", StringComparison.Ordinal)) return text == Help(command, nonce);
            return text.StartsWith((command == "pwrsi" ? "PWRSI STATUS " : "SLAVE STATUS ") + nonce + " | ", StringComparison.Ordinal) &&
                text.Contains(" | PROGRESS N/A") && !Commands.Any(c => text.Contains(c));
        }

        internal static void RunSelfTest()
        {
            const string nonce = "D234567";
            foreach (var command in Commands)
            {
                string matched;
                Need(TryMatchHash(TokenStore.Hash(command), out matched) && matched == command);
                Need(!TryMatchHash(TokenStore.Hash(command.ToUpperInvariant()), out matched));
            }
            foreach (var invalid in new[] { null, "Help", "help ", " total status", "total  status", "total\tstatus", "pwrsi.exe", "help unknown", "run calc", "help\n" })
                Need(!IsCommand(invalid));
            var state = new MachineStatus { Version = AppInfo.Version, LocalTime = new DateTime(2026, 9, 10, 12, 0, 0),
                UptimeMinutes = 1, AvailableMiB = 1024, TotalMiB = 2048,
                Processes = new ProcessInventory { Items = new[] { new ProcessState { Name = "help", Pid = 1 },
                    new ProcessState { Name = "m345678", Pid = 2 } } } };
            var total = FormatStatus("total status", nonce, state);
            Need(IsReply(total, "total status", nonce) && !total.Contains("help") && !total.Contains("M345678") && total.Contains("CPU=?%"));
            state.Processes.Items = new[] { new ProcessState { Name = "powersi", Pid = 3, CpuPermille = 42, WorkingSetMiB = 123, AgeSeconds = 120 } };
            state.PowerSi = PowerSiObservation.Parse("PS1:OK:RESUMED:38.000_MHZ:1:SIMULATION:38.000_MHZ:1:1");
            var pwrsi = FormatStatus("pwrsi", nonce, state);
            Need(pwrsi.Contains("POWERSI#3 CPU=4.2% RAM=123MIB AGE=2M") && pwrsi.Contains(state.PowerSi.Summary) &&
                !IsReply(pwrsi, "total status", nonce));
            Need(!FormatStatus("total status", nonce, state).Contains(state.PowerSi.Summary));
            state.Processes.Items = new ProcessState[0]; state.Processes.Unreadable = 2;
            state.PowerSi = PowerSiObservation.NotObserved();
            Need(FormatStatus("pwrsi", nonce, state).Contains("UNKNOWN 2 | PROGRESS N/A | NOT OBSERVED"));
            state.Processes.Items = Enumerable.Range(1, 128).Select(i => new ProcessState { Pid = i, Name = "powersi",
                CpuPermille = 1000, WorkingSetMiB = long.MaxValue / 1048576, AgeSeconds = 3155760000L }).ToArray();
            state.PowerSi = PowerSiObservation.Parse("PS1:OK:RESUMED:38.000_MHZ:1:SIMULATION:38.000_MHZ:1:1");
            var maximum = FormatStatus("pwrsi", nonce, state);
            Need(maximum.Length <= PcStatusReport.MaxPhoneLength && maximum.Contains("PROCS 4/128 OMIT 124"));
            state.PowerSi = PowerSiObservation.VisionResult("Simulation resumed.\nAFS Current Frequency ( MHz ) = 38.000", "Simulation",
                new DateTime(2026, 9, 11, 1, 2, 3, DateTimeKind.Utc));
            state.PowerSi.LocalEvidence = "PRIVATE SCREEN TEXT";
            state.PowerSi.LocalImage = new byte[] { 1, 2, 3 };
            var visionReply = FormatStatus("pwrsi", nonce, LinkProtocol.ParseStatus(LinkProtocol.FormatStatus(state, true), true));
            Need(IsReply(visionReply, "pwrsi", nonce) && visionReply.Contains("VISION MODEL_READ") &&
                visionReply.Contains("CAPTURE UTC 2026-09-11 01:02:03") && !visionReply.Contains("PRIVATE"));

            // Help never connects to the endpoint. It still needs an exact, locally approved one-use send consent.
            var endpoint = new SlaveEndpoint(System.Net.IPAddress.Loopback, 1, new string('0', 64), Convert.ToBase64String(new byte[32]));
            foreach (var command in Commands.Where(c => c.StartsWith("help", StringComparison.Ordinal)))
            {
                var consent = new SupervisedSendTest.Consent(nonce, true, true, endpoint, null, true);
                Need(consent.TryClaimRoundTrip()); consent.BindCommand(command);
                var reply = consent.PrepareReply();
                Need(IsReply(reply, command, nonce) && consent.IsAuthorizedReply(reply) &&
                    !consent.IsAuthorizedReply(reply + " ") && consent.TryConsume(reply) && !consent.TryConsume(reply));
                consent.Cancel(); Need(!consent.TryCommitMove());
            }
            var cancelled = new SupervisedSendTest.Consent(nonce, true, true, endpoint, null, true);
            Need(cancelled.TryClaimRoundTrip()); cancelled.BindCommand("help"); cancelled.Cancel();
            try { cancelled.PrepareReply(); throw new InvalidOperationException("Cancelled command prepared a reply."); }
            catch (MonitorException) { }
        }

        private static void Need(bool condition)
        { if (!condition) throw new MonitorException("COMMAND_INVALID", "Read-only command data rejected."); }
    }
}
