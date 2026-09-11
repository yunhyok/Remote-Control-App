using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteMonitorLink
{
    internal sealed class StatusServer : IDisposable
    {
        private const int ClientDeadlineMilliseconds = 8000;
        private readonly object gate = new object();
        private readonly SlaveIdentity identity;
        private readonly IPAddress address;
        private readonly int requestedPort;
        private readonly LocalVisionSettings vision;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private TcpListener listener;
        private TcpClient activeClient;
        private Task completion = Task.FromResult(0);
        private int boundPort;
        private bool started;
        private bool disposed;

        internal StatusServer(SlaveIdentity identity, IPAddress address, int port, LocalVisionSettings vision = null)
        {
            if (identity == null) throw new ArgumentNullException("identity");
            this.identity = identity;
            this.address = LinkProtocol.NormalizeAddress(address);
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException("port", "Invalid listener port.");
            requestedPort = port;
            this.vision = vision?.Clone();
        }

        internal event Action<string> Activity;
        internal event Action<MachineStatus> StatusCaptured;

        internal int Port { get { return Volatile.Read(ref boundPort); } }

        internal Task Completion
        {
            get { lock (gate) return completion; }
        }

        internal void Start()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("StatusServer");
                if (started) throw new InvalidOperationException("The status server is already started.");

                var created = new TcpListener(address, requestedPort);
                created.Start();
                listener = created;
                boundPort = ((IPEndPoint)created.LocalEndpoint).Port;
                started = true;
                completion = RunAsync(created);
            }
            Notify("LISTENING");
        }

        public void Dispose()
        {
            TcpListener listenerToStop;
            TcpClient clientToClose;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                listenerToStop = listener;
                clientToClose = activeClient;
            }
            Close(clientToClose);
            lifetime.Cancel();
            if (listenerToStop != null)
            {
                try { listenerToStop.Stop(); }
                catch (SocketException) { }
            }
        }

        private async Task RunAsync(TcpListener runningListener)
        {
            try
            {
                while (!IsDisposed())
                {
                    TcpClient client;
                    try
                    {
                        client = await runningListener.AcceptTcpClientAsync().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        if (IsDisposed()) break;
                        throw;
                    }
                    catch (SocketException)
                    {
                        if (IsDisposed()) break;
                        throw;
                    }

                    lock (gate)
                    {
                        if (disposed)
                        {
                            Close(client);
                            break;
                        }
                        activeClient = client;
                    }

                    Notify("CLIENT_CONNECTED");
                    try
                    {
                        await HandleClientAsync(client).ConfigureAwait(false);
                        Notify("STATUS_SENT");
                    }
                    catch (Exception exception)
                    {
                        if (!IsDisposed() && !IsClientFailure(exception)) throw;
                        Notify("CLIENT_REJECTED");
                    }
                    finally
                    {
                        lock (gate)
                            if (ReferenceEquals(activeClient, client)) activeClient = null;
                        Close(client);
                    }
                }
            }
            finally
            {
                Notify("STOPPED");
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            client.NoDelay = true;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            using (deadline.Token.Register(Close, client))
            using (var secure = new SslStream(client.GetStream(), false))
            {
                deadline.CancelAfter(ClientDeadlineMilliseconds);
                await LinkProtocol.AwaitWithCancellation(
                    secure.AuthenticateAsServerAsync(identity.Certificate, false, SslProtocols.Tls12, false),
                    deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();

                string request = await LinkProtocol.ReadLineAsync(
                    secure, LinkProtocol.MaxRequestLength, deadline.Token).ConfigureAwait(false);
                byte[] offeredToken;
                bool powerSiOnly;
                if (!LinkProtocol.TryParseRequest(request, out offeredToken, out powerSiOnly))
                    throw new InvalidDataException("Invalid status request.");

                byte[] expectedToken;
                if (!LinkProtocol.TryDecodeToken(identity.Token, out expectedToken))
                {
                    Array.Clear(offeredToken, 0, offeredToken.Length);
                    throw new CryptographicException("Invalid server identity.");
                }
                bool accepted;
                try
                {
                    accepted = LinkProtocol.FixedTimeEquals(offeredToken, expectedToken);
                }
                finally
                {
                    Array.Clear(offeredToken, 0, offeredToken.Length);
                    Array.Clear(expectedToken, 0, expectedToken.Length);
                }
                if (!accepted) throw new InvalidDataException("Status request rejected.");
                // Authentication still has the short deadline. Only an authenticated PWRSI request may wait for local inference.
                if (powerSiOnly) deadline.CancelAfter(PowerSiVision.RequestDeadlineMilliseconds);

                Notify("STATUS_CAPTURING");
                var status = MachineStatus.Capture();
                var processes = await ProcessInventory.CaptureAsync(deadline.Token, powerSiOnly).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                PowerSiObservation powerSi = null;
                if (powerSiOnly)
                {
                    Notify("PWRSI_CAPTURING");
                    powerSi = await PowerSiVision.CaptureAsync(processes, vision, deadline.Token).ConfigureAwait(false);
                    deadline.Token.ThrowIfCancellationRequested();
                }
                status.Processes = processes;
                status.PowerSiOnly = powerSiOnly;
                status.PowerSi = powerSi;
                string response = LinkProtocol.FormatStatus(status, powerSiOnly);
                var captured = StatusCaptured;
                if (captured != null)
                {
                    try { captured(status); }
                    catch { } // UI observers cannot change the protocol outcome.
                }
                await LinkProtocol.WriteLineAsync(
                    secure, response, LinkProtocol.MaxResponseLength, deadline.Token).ConfigureAwait(false);
            }
        }

        private bool IsDisposed()
        {
            lock (gate) return disposed;
        }

        private static bool IsClientFailure(Exception exception)
        {
            return exception is IOException || exception is SocketException ||
                exception is AuthenticationException || exception is InvalidDataException ||
                exception is OperationCanceledException || exception is ObjectDisposedException ||
                exception is CryptographicException;
        }

        private static void Close(object value)
        {
            Close(value as TcpClient);
        }

        private static void Close(TcpClient client)
        {
            if (client == null) return;
            try { client.Close(); }
            catch (SocketException) { }
        }

        private void Notify(string code)
        {
            Action<string> handler = Activity;
            if (handler == null) return;
            try { handler(code); }
            catch { }
        }
    }

    internal static class StatusClient
    {
        private const int QueryDeadlineMilliseconds = 8000;

        internal static async Task<MachineStatus> QueryAsync(SlaveEndpoint endpoint, CancellationToken cancellation,
            bool powerSiOnly = false)
        {
            if (endpoint == null) throw new ArgumentNullException("endpoint");
            cancellation.ThrowIfCancellationRequested();

            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (var client = new TcpClient(AddressFamily.InterNetwork))
            {
                deadline.CancelAfter(QueryDeadlineMilliseconds);
                using (deadline.Token.Register(Close, client))
                {
                    try
                    {
                        await LinkProtocol.AwaitWithCancellation(
                            client.ConnectAsync(endpoint.Address, endpoint.Port), deadline.Token).ConfigureAwait(false);
                        deadline.Token.ThrowIfCancellationRequested();
                        client.NoDelay = true;

                        byte[] expectedPin;
                        if (!LinkProtocol.TryDecodePin(endpoint.Pin, out expectedPin))
                            throw new InvalidDataException("Invalid status endpoint.");
                        try
                        {
                            int pinAccepted = 0;
                            RemoteCertificateValidationCallback validate = delegate(object sender, X509Certificate certificate,
                                X509Chain chain, SslPolicyErrors errors)
                            {
                                if (certificate == null) return false;
                                byte[] actualPin = null;
                                try
                                {
                                    using (SHA256 sha256 = SHA256.Create())
                                        actualPin = sha256.ComputeHash(certificate.GetRawCertData());
                                    bool equal = LinkProtocol.FixedTimeEquals(expectedPin, actualPin);
                                    if (equal) Interlocked.Exchange(ref pinAccepted, 1);
                                    return equal;
                                }
                                finally
                                {
                                    if (actualPin != null) Array.Clear(actualPin, 0, actualPin.Length);
                                }
                            };

                            using (var secure = new SslStream(client.GetStream(), false, validate))
                            {
                                await LinkProtocol.AwaitWithCancellation(
                                    secure.AuthenticateAsClientAsync("RemoteMonitorLink", null, SslProtocols.Tls12, false),
                                    deadline.Token).ConfigureAwait(false);
                                deadline.Token.ThrowIfCancellationRequested();
                                if (Interlocked.CompareExchange(ref pinAccepted, 0, 0) != 1)
                                    throw new AuthenticationException("Server certificate pin was not accepted.");
                                if (powerSiOnly) deadline.CancelAfter(PowerSiVision.RequestDeadlineMilliseconds + 10000);

                                // The shared token is deliberately unavailable to the wire until pinned TLS succeeds.
                                string request = LinkProtocol.CreateRequest(endpoint.Token, powerSiOnly);
                                await LinkProtocol.WriteLineAsync(
                                    secure, request, LinkProtocol.MaxRequestLength, deadline.Token).ConfigureAwait(false);
                                string response = await LinkProtocol.ReadLineAsync(
                                    secure, LinkProtocol.MaxResponseLength, deadline.Token).ConfigureAwait(false);
                                deadline.Token.ThrowIfCancellationRequested();
                                return LinkProtocol.ParseStatus(response, powerSiOnly);
                            }
                        }
                        finally
                        {
                            Array.Clear(expectedPin, 0, expectedPin.Length);
                        }
                    }
                    catch (Exception exception)
                    {
                        if (!deadline.IsCancellationRequested || !IsCancellationFailure(exception)) throw;
                        if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
                        throw new TimeoutException("Status query timed out.");
                    }
                }
            }
        }

        private static bool IsCancellationFailure(Exception exception)
        {
            return exception is OperationCanceledException || exception is IOException ||
                exception is SocketException || exception is ObjectDisposedException ||
                exception is AuthenticationException;
        }

        private static void Close(object value)
        {
            var client = value as TcpClient;
            if (client == null) return;
            try { client.Close(); }
            catch (SocketException) { }
        }
    }
}
