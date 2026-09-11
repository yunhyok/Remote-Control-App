using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteMonitorLink
{
    internal static class LinkSelfTest
    {
        internal static void Run()
        {
            MachineStatus.Capture().Validate();
            ProcessInventory.RunSelfTest();
            PowerSiObservation.SelfTest();
            PowerSiScreenCapture.SelfTest();
            OutputPaneImage.SelfTest();
            LocalVisionClient.SelfTest();
            TestMalformedParsing();

            string directory = Path.Combine(Path.GetTempPath(), "RemoteMonitorLink-" + Guid.NewGuid().ToString("N"));
            string identityPath = Path.Combine(directory, "identity.dat");
            Directory.CreateDirectory(directory);
            try
            {
                string persistedPin, persistedToken;
                using (SlaveIdentity first = SlaveIdentity.LoadOrCreate(identityPath))
                {
                    persistedPin = first.Pin;
                    persistedToken = first.Token;
                }

                using (SlaveIdentity transient = SlaveIdentity.Create())
                {
                    if (transient.Pin == persistedPin || transient.Token == persistedToken)
                        throw new InvalidOperationException("Transient identity reuse failed.");
                }

                using (SlaveIdentity identity = SlaveIdentity.LoadOrCreate(identityPath))
                {
                    if (identity.Pin != persistedPin || identity.Token != persistedToken)
                        throw new InvalidOperationException("Identity persistence failed.");
                    TestLoopback(identity);
                    TestActiveCancellation(identity);
                    TestDeadline(identity);
                }
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        private static void TestLoopback(SlaveIdentity identity)
        {
            var server = new StatusServer(identity, IPAddress.Loopback, 0);
            int sentCount = 0;
            int rejectedCount = 0;
            server.Activity += delegate(string code)
            {
                if (code == "STATUS_SENT") Interlocked.Increment(ref sentCount);
                if (code == "CLIENT_REJECTED") Interlocked.Increment(ref rejectedCount);
            };
            try
            {
                server.Start();
                SlaveEndpoint endpoint = SlaveEndpoint.Parse(identity.CreatePairing(IPAddress.Loopback, server.Port));
                MachineStatus status = StatusClient.QueryAsync(endpoint, CancellationToken.None).GetAwaiter().GetResult();
                status.Validate();
                if (status.Processes == null || status.PowerSiOnly || status.PowerSi != null)
                    throw new InvalidOperationException("Process snapshot scope was invalid over TLS.");

                MachineStatus powerSi = StatusClient.QueryAsync(endpoint, CancellationToken.None, true).GetAwaiter().GetResult();
                powerSi.Validate();
                if (powerSi.Processes == null || !powerSi.PowerSiOnly || powerSi.PowerSi == null || !powerSi.PowerSi.IsVision ||
                    powerSi.PowerSi.Code != "VISION_NOT_CONFIGURED" || powerSi.PowerSi.LocalImage != null || powerSi.PowerSi.LocalEvidence != null)
                    throw new InvalidOperationException("PowerSI snapshot missing over TLS.");
                foreach (ProcessState item in powerSi.Processes.Items)
                    if (!ProcessInventory.IsPowerSiName(item.Name))
                        throw new InvalidOperationException("PowerSI query returned an unrelated process.");

                string badPin = (endpoint.Pin[0] == '0' ? "1" : "0") + endpoint.Pin.Substring(1);
                ExpectFailure<AuthenticationException>(() => StatusClient.QueryAsync(
                    SlaveEndpoint.Parse(LinkProtocol.CreatePairing(endpoint.Address, endpoint.Port, badPin, endpoint.Token)),
                    CancellationToken.None), "Wrong certificate pin was accepted.");

                string badToken;
                using (SlaveIdentity other = SlaveIdentity.Create()) badToken = other.Token;
                ExpectFailure<IOException>(() => StatusClient.QueryAsync(
                    SlaveEndpoint.Parse(LinkProtocol.CreatePairing(endpoint.Address, endpoint.Port, endpoint.Pin, badToken)),
                    CancellationToken.None, true), "Wrong shared token was accepted.");

                if (server.Completion.IsCompleted)
                    throw new InvalidOperationException("Rejected client stopped the status server.");
                StatusClient.QueryAsync(endpoint, CancellationToken.None).GetAwaiter().GetResult().Validate();
                if (!SpinWait.SpinUntil(() => Volatile.Read(ref sentCount) == 3, 1000) ||
                    Volatile.Read(ref rejectedCount) != 2)
                    throw new InvalidOperationException("Status server activity accounting failed.");
            }
            finally
            {
                server.Dispose();
                if (!server.Completion.Wait(2000))
                    throw new InvalidOperationException("Status server did not stop promptly.");
            }
        }

        private static void TestActiveCancellation(SlaveIdentity identity)
        {
            var stalled = new TcpListener(IPAddress.Loopback, 0);
            TcpClient accepted = null;
            stalled.Start();
            Task<TcpClient> acceptTask = stalled.AcceptTcpClientAsync();
            try
            {
                int port = ((IPEndPoint)stalled.LocalEndpoint).Port;
                SlaveEndpoint endpoint = SlaveEndpoint.Parse(identity.CreatePairing(IPAddress.Loopback, port));
                var stopwatch = Stopwatch.StartNew();
                using (var cancellation = new CancellationTokenSource(100))
                {
                    try
                    {
                        StatusClient.QueryAsync(endpoint, cancellation.Token).GetAwaiter().GetResult();
                        throw new InvalidOperationException("Canceled status query completed.");
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
                stopwatch.Stop();
                if (stopwatch.ElapsedMilliseconds > 2500)
                    throw new InvalidOperationException("Status query cancellation was not prompt.");
                if (acceptTask.Status == TaskStatus.RanToCompletion) accepted = acceptTask.Result;
            }
            finally
            {
                if (accepted != null) accepted.Close();
                stalled.Stop();
            }
        }

        private static void TestDeadline(SlaveIdentity identity)
        {
            var stalled = new TcpListener(IPAddress.Loopback, 0);
            TcpClient accepted = null;
            stalled.Start();
            Task<TcpClient> acceptTask = stalled.AcceptTcpClientAsync();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                int port = ((IPEndPoint)stalled.LocalEndpoint).Port;
                SlaveEndpoint endpoint = SlaveEndpoint.Parse(identity.CreatePairing(IPAddress.Loopback, port));
                try
                {
                    StatusClient.QueryAsync(endpoint, CancellationToken.None).GetAwaiter().GetResult();
                    throw new InvalidOperationException("Timed status query completed.");
                }
                catch (TimeoutException)
                {
                }
                stopwatch.Stop();
                if (stopwatch.ElapsedMilliseconds < 7000 || stopwatch.ElapsedMilliseconds > 11000)
                    throw new InvalidOperationException("Status query deadline was not bounded.");
                if (acceptTask.Status == TaskStatus.RanToCompletion) accepted = acceptTask.Result;
            }
            finally
            {
                if (accepted != null) accepted.Close();
                stalled.Stop();
            }
        }

        private static void TestMalformedParsing()
        {
            const string pin = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            const string token = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
            string valid = LinkProtocol.CreatePairing(IPAddress.Loopback, 443, pin, token);
            SlaveEndpoint immutable = SlaveEndpoint.Parse(valid);
#pragma warning disable 618
            IPAddress returnedAddress = immutable.Address;
            returnedAddress.Address = IPAddress.Parse("10.0.0.1").Address;
#pragma warning restore 618
            if (!immutable.Address.Equals(IPAddress.Loopback))
                throw new InvalidOperationException("Approved endpoint address was mutable.");
            foreach (string invalid in new[]
            {
                null, string.Empty, valid + "|extra", valid + "\n", valid.Replace("127.0.0.1", "localhost"),
                valid.Replace("127.0.0.1", "127.1"), valid.Replace("|443|", "|0|"),
                valid.Replace(pin, pin.Substring(1)), valid.Replace(token, token.Substring(1)), new string('X', 161)
            })
                ExpectParseFailure(() => SlaveEndpoint.Parse(invalid));

            byte[] decoded;
            bool powerSiOnly;
            if (!LinkProtocol.TryParseRequest(LinkProtocol.CreateRequest(token), out decoded, out powerSiOnly) || powerSiOnly)
                throw new InvalidOperationException("STATUS request did not round-trip.");
            Array.Clear(decoded, 0, decoded.Length);
            if (!LinkProtocol.TryParseRequest(LinkProtocol.CreateRequest(token), out decoded))
                throw new InvalidOperationException("Legacy STATUS request parser failed.");
            Array.Clear(decoded, 0, decoded.Length);

            string powerSiRequest = LinkProtocol.CreateRequest(token, true);
            if (!LinkProtocol.TryParseRequest(powerSiRequest, out decoded, out powerSiOnly) || !powerSiOnly)
                throw new InvalidOperationException("PWRSI request did not round-trip.");
            Array.Clear(decoded, 0, decoded.Length);
            if (LinkProtocol.TryParseRequest(powerSiRequest, out decoded) || decoded != null ||
                LinkProtocol.TryParseRequest("RMS1|OTHER|" + token, out decoded, out powerSiOnly) || decoded != null)
                throw new InvalidOperationException("Unknown or wrong request operation was accepted.");

            MachineStatus sample = new MachineStatus
            {
                LocalTime = new DateTime(2026, 9, 10, 12, 34, 56),
                UptimeMinutes = 72001,
                AvailableMiB = 4096,
                TotalMiB = 8192,
                Version = LinkVersion.Value,
                Processes = new ProcessInventory()
            };
            string response = LinkProtocol.FormatStatus(sample);
            MachineStatus parsedStatus = LinkProtocol.ParseStatus(response);
            parsedStatus.Validate();
            if (parsedStatus.PowerSiOnly || parsedStatus.PowerSi != null) throw new InvalidOperationException("STATUS response scope was invalid.");
            ExpectParseFailure(() => LinkProtocol.FormatStatus(sample, true));
            sample.PowerSi = PowerSiObservation.Parse("PS1:OK:RESUMED:38.000_MHZ:1:SIMULATION:38.000_MHZ:1:1");
            string powerSiResponse = LinkProtocol.FormatStatus(sample, true);
            MachineStatus parsedPowerSi = LinkProtocol.ParseStatus(powerSiResponse, true);
            parsedPowerSi.Validate();
            if (!parsedPowerSi.PowerSiOnly || parsedPowerSi.PowerSi.Serialize() != sample.PowerSi.Serialize())
                throw new InvalidOperationException("PWRSI observation did not round-trip.");
            ExpectParseFailure(() => LinkProtocol.ParseStatus(response, true));
            ExpectParseFailure(() => LinkProtocol.ParseStatus(powerSiResponse));
            ExpectParseFailure(() => LinkProtocol.ParseStatus(
                powerSiResponse.Replace("|PWRSI|", "|OTHER|"), true));
            ExpectParseFailure(() => LinkProtocol.ParseStatus(powerSiResponse.Substring(0, powerSiResponse.LastIndexOf('|')), true));
            ExpectParseFailure(() => LinkProtocol.ParseStatus(powerSiResponse + "|extra", true));
            ExpectParseFailure(() => LinkProtocol.ParseStatus(powerSiResponse.Replace("PS1:OK", "PS1:EXECUTE"), true));

            var unrelated = new MachineStatus
            {
                LocalTime = sample.LocalTime,
                UptimeMinutes = sample.UptimeMinutes,
                AvailableMiB = sample.AvailableMiB,
                TotalMiB = sample.TotalMiB,
                Version = sample.Version,
                Processes = new ProcessInventory
                {
                    SessionId = 1,
                    Items = new[] { new ProcessState { Pid = 1, Name = "worker" } }
                }
            };
            ExpectParseFailure(() => LinkProtocol.FormatStatus(unrelated, true));
            string forgedPowerSi = LinkProtocol.FormatStatus(unrelated).Replace("|STATUS|", "|PWRSI|");
            ExpectParseFailure(() => LinkProtocol.ParseStatus(forgedPowerSi, true));
            foreach (string invalid in new[]
            {
                null, response + "\n", response + "|extra", response.Replace("4096|8192", "8193|8192"),
                response.Replace("2026-09-10", "2026-99-10"), response.Replace("|72001|", "|-1|"),
                response.Replace(LinkVersion.Value, "0.1.33"), new string('X', LinkProtocol.MaxResponseLength + 1),
                response.Substring(0, response.LastIndexOf('|'))
            })
                ExpectParseFailure(() => LinkProtocol.ParseStatus(invalid));
        }

        private static void ExpectFailure<TException>(Func<Task<MachineStatus>> action, string message)
            where TException : Exception
        {
            try
            {
                action().GetAwaiter().GetResult();
            }
            catch (TException)
            {
                return;
            }
            throw new InvalidOperationException(message);
        }

        private static void ExpectParseFailure(Action action)
        {
            try
            {
                action();
            }
            catch (FormatException)
            {
                return;
            }
            catch (InvalidDataException)
            {
                return;
            }
            throw new InvalidOperationException("Malformed protocol input was accepted.");
        }
    }
}
