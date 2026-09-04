using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RemoteMonitorMaster
{
    internal static class AppInfo
    {
        public const string Name = "Remote Monitor Master";
        public const string Version = "0.1.0";
        public const string Title = Name + " v" + Version;
        public const string InstanceMutexName = @"Local\RemoteMonitorMaster";
    }

    internal static class SystemInfo
    {
        public static readonly int FrameworkRelease = ReadFrameworkRelease();

        private static int ReadFrameworkRelease()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    return key == null ? 0 : Convert.ToInt32(key.GetValue("Release", 0), CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                return 0;
            }
        }
    }

    internal sealed class MonitorException : Exception
    {
        public string ReasonCode { get; private set; }

        public MonitorException(string reasonCode, string message)
            : base(message)
        {
            ReasonCode = reasonCode;
        }

        public MonitorException(string reasonCode, string message, Exception innerException)
            : base(message, innerException)
        {
            ReasonCode = reasonCode;
        }
    }

    internal static class Protocol
    {
        private static readonly Regex Ping = new Regex(
            @"\A!RM PING ([A-Za-z0-9][A-Za-z0-9._-]{0,63})\z",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static bool TryParsePing(string message, out string token)
        {
            token = null;
            if (message == null)
            {
                return false;
            }

            var match = Ping.Match(message);
            if (!match.Success)
            {
                return false;
            }

            token = match.Groups[1].Value;
            return true;
        }

        public static string Pong(string token)
        {
            return "[RM-OUT] PONG " + token;
        }
    }

    internal sealed class TokenStore
    {
        private static readonly Regex HashLine = new Regex(@"\A[A-F0-9]{64}\z", RegexOptions.CultureInvariant);
        private static readonly Mutex ReservationMutex = new Mutex(false, @"Local\RemoteMonitorMaster.TokenStore.v1");
        private readonly object sync = new object();
        private readonly string path;
        private readonly HashSet<string> hashes = new HashSet<string>(StringComparer.Ordinal);

        public TokenStore(string path)
        {
            this.path = path;
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                throw new InvalidDataException("Token state path has no directory.");
            }

            Directory.CreateDirectory(directory);
            if (!File.Exists(path))
            {
                return;
            }

            foreach (var rawLine in File.ReadAllLines(path, Encoding.ASCII))
            {
                var line = rawLine.Trim();
                if (!HashLine.IsMatch(line))
                {
                    throw new InvalidDataException("Token state contains an invalid record.");
                }

                hashes.Add(line);
            }
        }

        public bool TryReserve(string token)
        {
            var hash = Hash(token);
            lock (sync)
            {
                if (hashes.Contains(hash))
                {
                    return false;
                }

                var ownsMutex = false;
                try
                {
                    try
                    {
                        ownsMutex = ReservationMutex.WaitOne(TimeSpan.FromSeconds(5));
                    }
                    catch (AbandonedMutexException)
                    {
                        ownsMutex = true;
                    }

                    if (!ownsMutex)
                    {
                        throw new IOException("Timed out waiting for the token reservation lock.");
                    }

                    using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                    {
                        using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                line = line.Trim();
                                if (!HashLine.IsMatch(line))
                                {
                                    throw new InvalidDataException("Token state contains an invalid record.");
                                }

                                hashes.Add(line);
                            }
                        }

                        if (hashes.Contains(hash))
                        {
                            return false;
                        }

                        var bytes = Encoding.ASCII.GetBytes(hash + Environment.NewLine);
                        stream.Seek(0, SeekOrigin.End);
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                }
                finally
                {
                    if (ownsMutex)
                    {
                        ReservationMutex.ReleaseMutex();
                    }
                }

                hashes.Add(hash);
                return true;
            }
        }

        public static string Hash(string token)
        {
            using (var sha256 = SHA256.Create())
            {
                var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(token));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var value in bytes)
                {
                    builder.Append(value.ToString("X2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }
    }

    internal sealed class AuditLog : IDisposable
    {
        internal struct LogField
        {
            public readonly string Key;
            public readonly object Value;

            public LogField(string key, object value)
            {
                Key = key;
                Value = value;
            }
        }

        private readonly object sync = new object();
        private readonly StreamWriter writer;
        private readonly byte[] fingerprintKey;

        public string FolderPath { get; private set; }
        public string FilePath { get; private set; }

        public AuditLog()
        {
            fingerprintKey = new byte[32];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(fingerprintKey);
            }

            FolderPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RemoteMonitorMaster",
                "logs");
            Directory.CreateDirectory(FolderPath);
            FilePath = Path.Combine(
                FolderPath,
                "remote-monitor-master-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                "-pid" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + ".log");
            writer = new StreamWriter(
                new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false));
            writer.AutoFlush = true;
        }

        public static LogField Field(string key, object value)
        {
            return new LogField(key, value);
        }

        public string Fingerprint(string value)
        {
            using (var hmac = new HMACSHA256(fingerprintKey))
            {
                var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var item in bytes)
                {
                    builder.Append(item.ToString("X2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }

        public void Write(string level, string code, params LogField[] fields)
        {
            var utc = DateTime.UtcNow;
            var local = DateTime.Now;
            var line = new StringBuilder(256)
                .Append("utc=\"").Append(Escape(utc.ToString("o", CultureInfo.InvariantCulture))).Append("\"\t")
                .Append("local=\"").Append(Escape(local.ToString("o", CultureInfo.InvariantCulture))).Append("\"\t")
                .Append("app=\"").Append(AppInfo.Name).Append("\"\t")
                .Append("version=\"").Append(AppInfo.Version).Append("\"\t")
                .Append("level=\"").Append(Escape(level)).Append("\"\t")
                .Append("code=\"").Append(Escape(code)).Append('"');

            foreach (var field in fields)
            {
                line.Append('\t')
                    .Append(field.Key)
                    .Append("=\"")
                    .Append(Escape(Convert.ToString(field.Value, CultureInfo.InvariantCulture)))
                    .Append('"');
            }

            lock (sync)
            {
                writer.WriteLine(line.ToString());
            }
        }

        public void WriteException(string code, Exception exception, params LogField[] fields)
        {
            var combined = new LogField[fields.Length + 2];
            Array.Copy(fields, combined, fields.Length);
            combined[fields.Length] = Field("exception_type", exception.GetType().FullName);
            combined[fields.Length + 1] = Field("hresult", "0x" + exception.HResult.ToString("X8", CultureInfo.InvariantCulture));
            Write("ERROR", code, combined);
        }

        public void Dispose()
        {
            lock (sync)
            {
                writer.Dispose();
            }
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }

    }

    internal static class NativeMethods
    {
        private static readonly Guid WinTrustActionGenericVerifyV2 =
            new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint Size;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            public uint Size;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr FileInfo;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProviderFlags;
            public uint UiContext;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern uint WinVerifyTrust(
            IntPtr window,
            [In] ref Guid actionId,
            [In, Out] ref WinTrustData trustData);

        public static string WindowClass(IntPtr window)
        {
            var builder = new StringBuilder(256);
            var length = GetClassName(window, builder, builder.Capacity);
            if (length <= 0)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            return builder.ToString();
        }

        public static uint VerifyAuthenticode(string path)
        {
            var fileInfo = new WinTrustFileInfo
            {
                Size = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)),
                FilePath = path
            };
            var fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(WinTrustFileInfo)));
            var marshaled = false;
            try
            {
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
                marshaled = true;
                var trustData = new WinTrustData
                {
                    Size = (uint)Marshal.SizeOf(typeof(WinTrustData)),
                    UiChoice = 2,
                    RevocationChecks = 0,
                    UnionChoice = 1,
                    FileInfo = fileInfoPointer,
                    StateAction = 1,
                    ProviderFlags = 0x10 | 0x1000,
                    UiContext = 0
                };
                var action = WinTrustActionGenericVerifyV2;
                var result = WinVerifyTrust(IntPtr.Zero, ref action, ref trustData);
                trustData.StateAction = 2;
                WinVerifyTrust(IntPtr.Zero, ref action, ref trustData);
                return result;
            }
            finally
            {
                if (marshaled)
                {
                    Marshal.DestroyStructure(fileInfoPointer, typeof(WinTrustFileInfo));
                }

                Marshal.FreeCoTaskMem(fileInfoPointer);
            }
        }
    }

    internal static class CoreSelfTest
    {
        public static int Run()
        {
            var directory = Path.Combine(Path.GetTempPath(), "RemoteMonitorMaster-selftest-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "seen-tokens.txt");
            try
            {
                string token;
                Require(Protocol.TryParsePing("!RM PING abc-123", out token), "valid PING was rejected");
                Require(token == "abc-123", "token changed");
                Require(!Protocol.TryParsePing(" !RM PING abc-123", out token), "leading space was accepted");
                Require(!Protocol.TryParsePing("!RM  PING abc-123", out token), "non-canonical spacing was accepted");
                Require(!Protocol.TryParsePing("!RM PING abc-123\r\n", out token), "newline-terminated PING was accepted");
                Require(!Protocol.TryParsePing("[RM-OUT] PONG abc-123", out token), "own output was parsed");
                Require(!Protocol.TryParsePing("2026-09-04 12:00", out token), "timestamp was parsed");
                Require(!Protocol.TryParsePing("!RM PING report.pdf extra", out token), "file-like extra text was parsed");
                Require(Protocol.TryParsePing("!RM PING profile-1", out token), "valid token containing 'file' was rejected");
                Require(Protocol.Pong("abc-123") == "[RM-OUT] PONG abc-123", "PONG format changed");
                Require(!AutomationTarget.MatchesInputMetadata("messageSearch", "Edit"), "message search decoy matched composer input");
                Require(AutomationTarget.MatchesInputMetadata("messageInput", "Edit"), "explicit composer input metadata was rejected");
                Require(AppInfo.InstanceMutexName.IndexOf(AppInfo.Version, StringComparison.Ordinal) < 0,
                    "single-instance mutex must remain stable across versions");

                var store = new TokenStore(path);
                var concurrentStore = new TokenStore(path);
                Require(store.TryReserve("abc-123"), "first token was rejected");
                Require(!concurrentStore.TryReserve("abc-123"), "concurrently opened duplicate store accepted a token");
                Require(!store.TryReserve("abc-123"), "duplicate token was accepted");
                Require(!new TokenStore(path).TryReserve("abc-123"), "persisted duplicate was accepted");
                Require(NativeMethods.VerifyAuthenticode(path) != 0, "an unsigned state file was reported as trusted");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory);
                }
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Self-test failed: " + message);
            }
        }
    }
}
