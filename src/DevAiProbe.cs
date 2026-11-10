#if DEV
using System;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;

namespace CivicPressure
{
    // DEV transport. Probe sends no credentials; Generate is explicitly user-triggered.
    internal sealed class DevAiProbe
    {
        internal volatile string Result;
        internal string Diagnostic;
        internal bool HttpsReached;
        private static int running;
        private HttpWebRequest request;
        private readonly object gate = new object();
        private volatile bool cancelled;
        private readonly bool gemini;
        internal DevAiProbe() { }
        internal DevAiProbe(bool useGemini) { gemini = useGemini; }
        internal string Host { get { return gemini ? "generativelanguage.googleapis.com" : "api.openai.com"; } }
        internal string GenerationPath { get { return gemini ? "/v1beta/models/" + DevAiReply.GeminiModel + ":generateContent" : "/v1/responses"; } }
        internal static bool IsRunning { get { return Interlocked.CompareExchange(ref running, 0, 0) != 0; } }

        internal void Generate(string key, string body)
        {
            if (cancelled) { Result = "Cancelled"; return; }
            if (!DevAiReply.ValidKey(key)) { Result = "InvalidKey"; return; }
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) { Result = "WindowsOnly"; return; }
            if (body == null || body.Length > 16384) { Result = "InvalidInput"; return; }
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) { Result = "Busy"; return; }
            string result;
            try { result = RunWindows(key, body); }
            catch (Exception) { result = "RequestFailed"; }
            finally { key = null; body = null; Interlocked.Exchange(ref running, 0); }
            Result = cancelled ? "Cancelled" : result;
        }

        internal void Cancel()
        {
            lock (gate) { cancelled = true; if (request != null) request.Abort(); }
        }

        internal void Run()
        {
            if (cancelled) { Result = "Cancelled"; return; }
            // A hidden panel must not leave a second probe running alongside the first.
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) { Result = "Busy"; return; }
            string result;
            try
            {
                if (gemini)
                {
                    result = Environment.OSVersion.Platform == PlatformID.Win32NT ? RunWindows() : "WindowsOnly";
                    Diagnostic = "Google: " + result;
                    HttpsReached = result == "HTTP 400" || result == "HTTP 401" || result == "HTTP 403";
                }
                else
                {
                string managed = RunManaged();
                Diagnostic = "Managed: " + managed;
                result = Diagnostic;
                HttpsReached = managed == "HTTP 401";
                if (!cancelled && !managed.StartsWith("HTTP ") && Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    string native = RunWindows();
                    result = "Managed: " + managed.Split('/')[0] + "\nWindows: " + native;
                    Diagnostic += "\nWindows: " + native;
                    HttpsReached = native == "HTTP 401";
                }
                }
            }
            catch (Exception error) { result = error.GetType().Name; Diagnostic = result; }
            finally { Interlocked.Exchange(ref running, 0); }
            Result = cancelled ? "Cancelled" : result;
        }

        private string RunManaged()
        {
            Timer deadline = null;
            try
            {
                // A pre-existing callback could bypass validation; do not call that a safe probe.
                if (ServicePointManager.ServerCertificateValidationCallback != null)
                { return "CertificateValidationOverride"; }
                HttpWebRequest current = (HttpWebRequest)WebRequest.Create("https://api.openai.com/v1/models");
                current.Method = "GET"; current.AllowAutoRedirect = false;
                current.Timeout = 15000; current.ReadWriteTimeout = 15000;
                current.KeepAlive = false; current.UseDefaultCredentials = false;
                lock (gate) { if (cancelled) return "Cancelled"; request = current; }
                deadline = new Timer(delegate { Cancel(); }, null, 20000, Timeout.Infinite);
                using (HttpWebResponse response = (HttpWebResponse)current.GetResponse())
                    return "HTTP " + (int)response.StatusCode;
            }
            catch (WebException error)
            {
                HttpWebResponse response = error.Response as HttpWebResponse;
                if (response != null) { using (response) return "HTTP " + (int)response.StatusCode; }
                return Describe(error);
            }
            catch (Exception error) { return Describe(error); }
            finally { if (deadline != null) deadline.Dispose(); lock (gate) { request = null; } }
        }

        // Only types/status codes: never copy exception messages, keys, or response bodies to logs.
        internal static string Describe(Exception error)
        {
            string value = "";
            for (int i = 0; error != null && i < 4; i++, error = error.InnerException)
            {
                WebException web = error as WebException;
                value += (i == 0 ? "" : "/") + (web == null ? error.GetType().Name : web.Status.ToString());
            }
            return value;
        }

        private string RunWindows()
        { return RunWindows(null, null); }

        private string RunWindows(string key, string body)
        {
            IntPtr session = IntPtr.Zero, server = IntPtr.Zero, message = IntPtr.Zero;
            IntPtr payload = IntPtr.Zero;
            bool generation = body != null;
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            string stage = "Open";
            try
            {
                if (cancelled) return "Cancelled";
                session = WinHttpOpen("CivicPressure-DEV-Probe", 0, null, null, 0);
                if (session == IntPtr.Zero) return NativeError(stage);
                stage = "Options";
                // Session-local TLS 1.2; OS certificate verification remains enabled.
                if (!Option(session, 84, 0x800) || !WinHttpSetTimeouts(session, 3000, 5000, 5000, generation ? 30000 : 3000))
                    return NativeError(stage);
                stage = "Connect";
                server = WinHttpConnect(session, Host, 443, 0);
                if (server == IntPtr.Zero) return NativeError(stage);
                stage = "Request";
                string path = generation ? GenerationPath : (gemini ? "/v1beta/models" : "/v1/models");
                message = WinHttpOpenRequest(server, generation ? "POST" : "GET", path, null, null, IntPtr.Zero, 0x00800000);
                if (message == IntPtr.Zero) return NativeError(stage);
                // Never redirect, send Windows credentials, or retain cookies.
                stage = "Options";
                if (!Option(message, 88, 0) || !Option(message, 77, 2) || !Option(message, 63, 1))
                    return NativeError(stage);
                if (cancelled) return "Cancelled";
                stage = "Send";
                uint size = 0; string headers = null;
                if (generation)
                {
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(body); size = (uint)bytes.Length;
                    payload = Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes, 0, payload, bytes.Length);
                    headers = "Content-Type: application/json\r\n" + (gemini ? "x-goog-api-key: " : "Authorization: Bearer ") + key + "\r\n";
                }
                if (!WinHttpSendRequest(message, headers, headers == null ? 0 : (uint)headers.Length, payload, size, size, IntPtr.Zero)) return NativeError(stage);
                headers = null; key = null;
                if (cancelled) return "Cancelled";
                stage = "Receive";
                if (!WinHttpReceiveResponse(message, IntPtr.Zero)) return NativeError(stage);
                uint status = 0, length = 4;
                stage = "Status";
                if (!WinHttpQueryHeaders(message, 19 | 0x20000000, null, ref status, ref length, IntPtr.Zero))
                    return NativeError(stage);
                if (!generation) return "HTTP " + status;
                using (System.IO.MemoryStream data = new System.IO.MemoryStream())
                {
                    byte[] buffer = new byte[4096]; uint read;
                    while (true)
                    {
                        if (cancelled) return "Cancelled";
                        if (clock.ElapsedMilliseconds > 60000) return "Timeout";
                        if (!WinHttpReadData(message, buffer, (uint)buffer.Length, out read)) return NativeError("Read");
                        if (read == 0) break;
                        if (data.Length + read > 65536) return "ResponseTooLarge";
                        data.Write(buffer, 0, (int)read);
                    }
                    try
                    {
                        string json = System.Text.Encoding.UTF8.GetString(data.ToArray());
                        return gemini ? DevAiReply.ParseGemini(json, (int)status) : DevAiReply.Parse(json, (int)status);
                    }
                    catch (Exception) { return status == 200 ? "InvalidResponse" : "HTTP " + status; }
                }
            }
            finally
            {
                // Synchronous WinHTTP handles are closed only by their worker after calls return.
                // Cancel suppresses results; bounded native timeouts finish in-flight work.
                if (message != IntPtr.Zero) WinHttpCloseHandle(message);
                if (server != IntPtr.Zero) WinHttpCloseHandle(server);
                if (session != IntPtr.Zero) WinHttpCloseHandle(session);
                if (payload != IntPtr.Zero) Marshal.FreeHGlobal(payload);
            }
        }

        private static string NativeError(string stage) { return stage + " WinHTTP " + Marshal.GetLastWin32Error(); }
        private static bool Option(IntPtr handle, uint name, uint value) { return WinHttpSetOption(handle, name, ref value, 4); }

        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr WinHttpOpen(string agent, uint access, string proxy, string bypass, uint flags);
        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr WinHttpConnect(IntPtr session, string host, ushort port, uint reserved);
        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr WinHttpOpenRequest(IntPtr server, string verb, string path, string version, string referer, IntPtr types, uint flags);
        [DllImport("winhttp.dll", SetLastError = true)]
        private static extern bool WinHttpSetOption(IntPtr handle, uint option, ref uint value, uint size);
        [DllImport("winhttp.dll", SetLastError = true)]
        private static extern bool WinHttpSetTimeouts(IntPtr handle, int resolve, int connect, int send, int receive);
        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern bool WinHttpSendRequest(IntPtr handle, string headers, uint headerLength, IntPtr body, uint size, uint total, IntPtr context);
        [DllImport("winhttp.dll", SetLastError = true)]
        private static extern bool WinHttpReceiveResponse(IntPtr handle, IntPtr reserved);
        [DllImport("winhttp.dll", SetLastError = true)]
        private static extern bool WinHttpReadData(IntPtr handle, byte[] buffer, uint size, out uint read);
        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern bool WinHttpQueryHeaders(IntPtr handle, uint info, string name, ref uint value, ref uint size, IntPtr index);
        [DllImport("winhttp.dll")]
        private static extern bool WinHttpCloseHandle(IntPtr handle);
    }
}
#endif
