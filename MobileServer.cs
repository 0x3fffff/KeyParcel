using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace KeyParcel
{
    internal sealed class MobileInbox
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string source, string destination, uint flags);
        private const uint ReplaceExisting = 0x1, WriteThrough = 0x8;
        private readonly string credentialFile;
        private readonly object gate = new object();
        private string credential, pairCode, latest;
        private DateTime expiry;

        internal MobileInbox(string folder)
        {
            credentialFile = Path.Combine(folder, "phone-credential.txt");
            try
            {
                var saved = File.ReadAllText(credentialFile);
                if (saved.Length == 43) credential = saved;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal string Latest { get { lock (gate) return latest; } }
        internal bool Paired { get { lock (gate) return credential != null; } }

        internal string PairCode
        {
            get { lock (gate) return DateTime.UtcNow < expiry ? pairCode : null; }
        }

        internal void NewCode()
        {
            lock (gate)
            {
                pairCode = RandomToken(16);
                expiry = DateTime.UtcNow.AddMinutes(5);
            }
        }

        internal void CancelCode() { lock (gate) pairCode = null; }

        internal void Forget()
        {
            lock (gate)
            {
                File.Delete(credentialFile);
                credential = pairCode = latest = null;
            }
        }

        internal string Pair(string submitted)
        {
            lock (gate)
            {
                if (DateTime.UtcNow >= expiry || !Matches(pairCode, submitted)) return null;
                var next = RandomToken(32);
                SaveSetting(credentialFile, next);
                credential = next;
                pairCode = null;
                return next;
            }
        }

        internal bool Accept(string submitted, string value)
        {
            lock (gate)
            {
                if (!Matches(credential, submitted)) return false;
                if (value != null) latest = value;
                return true;
            }
        }

        internal static void SaveSetting(string path, string value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, value, new UTF8Encoding(false));
                if (!MoveFileEx(temp, path, ReplaceExisting | WriteThrough))
                    throw new IOException("Could not replace the settings file.", new Win32Exception(Marshal.GetLastWin32Error()));
            }
            finally { File.Delete(temp); }
        }

        private static string RandomToken(int size)
        {
            var data = new byte[size];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(data);
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static bool Matches(string expected, string actual)
        {
            if (expected == null || actual == null || expected.Length != actual.Length) return false;
            int diff = 0;
            for (int i = 0; i < expected.Length; i++) diff |= expected[i] ^ actual[i];
            return diff == 0;
        }
    }

    internal sealed class MobileServer : IDisposable
    {
        internal const int PreferredPort = 8765;
        private const int MaxTextBytes = 32768;
        private const int MaxHeaderBytes = 8192;
        private const int RequestTimeoutMs = 6000;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly object gate = new object();
        private readonly TcpListener listener;
        private readonly MobileInbox inbox;
        private readonly byte[] page;
        private readonly string authority;
        private readonly HashSet<TcpClient> clients = new HashSet<TcpClient>();
        private bool closed;
        internal string Address { get { return "http://" + authority; } }
        internal int ListeningPort { get; private set; }

        internal MobileServer(IPAddress ip, int port, MobileInbox inbox)
        {
            this.inbox = inbox;
            using (var resource = typeof(MobileServer).Assembly.GetManifestResourceStream("KeyParcel.Phone.html"))
            using (var buffer = new MemoryStream())
            {
                resource.CopyTo(buffer);
                page = buffer.ToArray();
            }
            try { listener = Bind(ip, port); }
            catch (SocketException error)
            {
                if (port == 0 || (error.SocketErrorCode != SocketError.AddressAlreadyInUse &&
                    error.SocketErrorCode != SocketError.AccessDenied)) throw;
                // Port zero lets Windows select and reserve a free port in a single operation.
                listener = Bind(ip, 0);
            }
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            ListeningPort = endpoint.Port;
            authority = endpoint.ToString();
            var acceptor = new Thread(AcceptConnections) { IsBackground = true, Name = "KeyParcel LAN" };
            acceptor.Start();
        }

        private static TcpListener Bind(IPAddress ip, int port)
        {
            var candidate = new TcpListener(ip, port);
            try
            {
                candidate.ExclusiveAddressUse = true;
                candidate.Start(8);
                return candidate;
            }
            catch
            {
                candidate.Server.Close();
                throw;
            }
        }

        private void AcceptConnections()
        {
            while (true)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (SocketException) { return; }
                catch (InvalidOperationException) { return; }
                lock (gate)
                {
                    if (closed || clients.Count >= 8) { client.Close(); continue; }
                    clients.Add(client);
                }
                ThreadPool.QueueUserWorkItem(ServeClient, client);
            }
        }

        private void ServeClient(object state)
        {
            var client = (TcpClient)state;
            try
            {
                client.SendTimeout = 3000;
                using (var stream = client.GetStream()) Handle(stream);
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                client.Close();
                lock (gate) clients.Remove(client);
            }
        }

        private void Handle(NetworkStream stream)
        {
            var request = new RequestReader(stream);
            string[] line = request.Line().Split(' ');
            if (line.Length != 3 || (line[2] != "HTTP/1.1" && line[2] != "HTTP/1.0"))
                throw new IOException("Invalid request line");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string header;
            while ((header = request.Line()).Length != 0)
            {
                int split = header.IndexOf(':');
                if (split < 1 || headers.ContainsKey(header.Substring(0, split)))
                    throw new IOException("Invalid request header");
                headers.Add(header.Substring(0, split), header.Substring(split + 1).Trim());
            }

            string host;
            if (!headers.TryGetValue("Host", out host) || host != authority ||
                headers.ContainsKey("Transfer-Encoding"))
            {
                Respond(stream, "400 Bad Request");
                return;
            }
            if (line[0] == "GET" && line[1] == "/")
            {
                Respond(stream, "200 OK", page, "text/html; charset=utf-8");
                return;
            }
            if (line[0] != "POST" || (line[1] != "/pair" && line[1] != "/check" && line[1] != "/text"))
            {
                Respond(stream, "404 Not Found");
                return;
            }

            string origin, authorization;
            if (!headers.TryGetValue("Origin", out origin) || origin != Address ||
                !headers.TryGetValue("Authorization", out authorization) ||
                !authorization.StartsWith("Bearer ", StringComparison.Ordinal))
            {
                Respond(stream, "403 Forbidden");
                return;
            }
            string size;
            int length;
            if (!headers.TryGetValue("Content-Length", out size) ||
                !Int32.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out length))
            {
                Respond(stream, "400 Bad Request");
                return;
            }
            if (length > MaxTextBytes)
            {
                Respond(stream, "413 Content Too Large");
                return;
            }
            bool textRequest = line[1] == "/text";
            if (textRequest != (length > 0))
            {
                Respond(stream, "400 Bad Request");
                return;
            }
            string value = null;
            if (textRequest)
            {
                try { value = Utf8.GetString(request.Body(length)); }
                catch (DecoderFallbackException) { Respond(stream, "400 Bad Request"); return; }
                if (value.IndexOf('\0') >= 0) { Respond(stream, "400 Bad Request"); return; }
            }

            string token = authorization.Substring(7);
            string newCredential = null;
            bool accepted;
            try
            {
                lock (gate)
                {
                    if (closed) return;
                    if (line[1] == "/pair")
                    {
                        newCredential = inbox.Pair(token);
                        accepted = newCredential != null;
                    }
                    else accepted = inbox.Accept(token, value);
                }
            }
            catch (IOException) { Respond(stream, "500 Internal Server Error"); return; }
            catch (UnauthorizedAccessException) { Respond(stream, "500 Internal Server Error"); return; }
            if (!accepted) Respond(stream, "403 Forbidden");
            else Respond(stream, "200 OK", newCredential == null ? null : Utf8.GetBytes(newCredential));
        }

        private static void Respond(NetworkStream stream, string status, byte[] body = null,
                                    string type = "text/plain; charset=utf-8")
        {
            string headers = "HTTP/1.1 " + status + "\r\nConnection: close\r\nCache-Control: no-store\r\n" +
                "X-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\n" +
                "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; " +
                "script-src 'unsafe-inline'; connect-src 'self'; form-action 'none'; frame-ancestors 'none'\r\n" +
                "Content-Type: " + type + "\r\nContent-Length: " + (body == null ? 0 : body.Length) + "\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(headers);
            stream.Write(bytes, 0, bytes.Length);
            if (body != null) stream.Write(body, 0, body.Length);
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (closed) return;
                closed = true;
                listener.Stop();
                foreach (var client in clients) client.Close();
                inbox.CancelCode();
            }
        }

        private sealed class RequestReader
        {
            private readonly NetworkStream stream;
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private readonly byte[] buffer = new byte[4096];
            private int index, count, headerBytes;

            internal RequestReader(NetworkStream stream) { this.stream = stream; }

            internal string Line()
            {
                var line = new StringBuilder();
                while (++headerBytes <= MaxHeaderBytes)
                {
                    int value = Next();
                    if (value == '\r')
                    {
                        headerBytes++;
                        if (Next() != '\n') break;
                        return line.ToString();
                    }
                    if (value < 32 && value != '\t' || value >= 127) break;
                    line.Append((char)value);
                }
                throw new IOException("Invalid or oversized header");
            }

            internal byte[] Body(int length)
            {
                var body = new byte[length];
                for (int i = 0; i < body.Length; i++) body[i] = (byte)Next();
                return body;
            }

            private int Next()
            {
                if (index == count)
                {
                    long remaining = RequestTimeoutMs - clock.ElapsedMilliseconds;
                    if (remaining <= 0) throw new IOException("Request timed out");
                    stream.ReadTimeout = (int)remaining;
                    count = stream.Read(buffer, 0, buffer.Length);
                    index = 0;
                    if (count == 0) throw new EndOfStreamException();
                }
                return buffer[index++];
            }
        }
    }
}
