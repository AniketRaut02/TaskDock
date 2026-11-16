using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TaskDock.Unity
{
    /// <summary>
    /// ClientWebSocket on a background thread: outbound queue, heartbeats carrying the main-thread pulse,
    /// reconnect with 1 s → 5 s backoff. Never touches Unity APIs (it keeps running while the main thread is blocked).
    /// </summary>
    static class HubClient
    {
        static readonly object SendLock = new object();
        static readonly ConcurrentQueue<string> Outbox = new ConcurrentQueue<string>();
        static readonly AutoResetEvent Wake = new AutoResetEvent(false);

        static Thread _thread;
        static volatile bool _stop;
        static volatile ClientWebSocket _ws;
        static volatile string _lastState; // resent after every reconnect: snapshots, never deltas
        static string _instance;
        static JObject _hello;
        static int _portOverride;

        public static bool Connected => _ws != null && _ws.State == WebSocketState.Open;
        /// <summary>Why the last attempt failed, for the Preferences page; null once connected.</summary>
        public static string LastError { get; private set; }
        static string _logged; // log each distinct failure once, not every retry

        /// <summary>Debug.Log is thread-safe; this thread never touches other Unity APIs.</summary>
        static void Fail(string why)
        {
            LastError = why;
            if (why == _logged) return;
            _logged = why;
            UnityEngine.Debug.LogWarning("[TaskDock] Can't reach the TaskDock widget: " + why + " Retrying every few seconds.");
        }
        public static string Instance => _instance;

        /// <summary>Raised on the socket thread; CommandDispatcher moves it to the main thread.</summary>
        public static event Action<string, string> Command;

        public static void Start(string instance, JObject hello, int portOverride)
        {
            _instance = instance;
            _hello = hello;
            _portOverride = portOverride;
            _stop = false;
            _thread = new Thread(Run) { IsBackground = true, Name = "TaskDock hub client" };
            _thread.Start();
        }

        public static void SendState(JObject body)
        {
            var frame = Wire.Frame("state", _instance, body);
            _lastState = frame;
            Outbox.Enqueue(frame);
            Wake.Set();
        }

        public static void SendResult(string id, bool ok, string error)
        {
            var b = new JObject { ["id"] = id, ["ok"] = ok };
            if (error != null) b["error"] = error;
            Outbox.Enqueue(Wire.Frame("result", _instance, b));
            Wake.Set();
        }

        /// <summary>
        /// Synchronous send for the frame right before a domain reload: the socket dies with the domain,
        /// so this can't wait for the background thread. Capped so it never stalls the reload.
        /// </summary>
        public static void SendNow(JObject stateBody, int timeoutMs = 250)
        {
            var frame = Wire.Frame("state", _instance, stateBody);
            _lastState = frame;
            Send(_ws, frame, timeoutMs);
        }

        public static void Stop(string byeReason)
        {
            _stop = true;
            var ws = _ws;
            if (byeReason != null) Send(ws, Wire.Frame("bye", _instance, new JObject { ["reason"] = byeReason }), 250);
            try { ws?.Abort(); } catch (Exception) { }
            Wake.Set();
            _thread?.Join(500); // a live thread across a domain reload can stall the Editor
        }

        static void Run()
        {
            var backoff = 1000;
            while (!_stop)
            {
                try { Session(); backoff = 1000; }
                catch (Exception e) { Fail(e.GetBaseException().GetType().Name + ": " + e.GetBaseException().Message); }
                _ws = null;
                if (_stop) break;
                Wake.WaitOne(backoff);
                backoff = Math.Min(backoff * 2, 5000);
            }
        }

        static void Session()
        {
            var hub = Wire.ReadHub(out var error);
            if (hub == null) { Fail(error); return; }
            var port = _portOverride > 0 ? _portOverride : hub.Value.Port;

            using (var ws = new ClientWebSocket())
            {
                if (!ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), CancellationToken.None).Wait(3000)) { Fail($"no answer on port {port} within 3 s."); return; }
                var hello = (JObject)_hello.DeepClone();
                hello["token"] = hub.Value.Token;
                Send(ws, Wire.Frame("hello", _instance, hello), 2000);

                var welcome = Receive(ws, 3000);
                if (welcome == null || (string)welcome["type"] != "welcome") { Fail("the widget refused the hello (token changed? reconnecting)."); return; }
                var heartbeatMs = (int?)welcome["body"]?["heartbeatMs"] ?? 1000;

                _ws = ws;
                if (_logged != "connected") UnityEngine.Debug.Log($"[TaskDock] Connected to the TaskDock widget on port {port}.");
                LastError = null;
                _logged = "connected";
                if (_lastState != null) Send(ws, _lastState, 2000); // full snapshot after every (re)connect
                while (Outbox.TryDequeue(out _)) { } // anything queued while offline is older than that snapshot

                var reader = Task.Run(() => ReadLoop(ws));
                var nextBeat = Stopwatch.StartNew();
                while (!_stop && ws.State == WebSocketState.Open && !reader.IsCompleted)
                {
                    while (Outbox.TryDequeue(out var frame)) Send(ws, frame, 2000);
                    if (nextBeat.ElapsedMilliseconds >= heartbeatMs)
                    {
                        nextBeat.Restart();
                        Send(ws, Wire.Frame("heartbeat", _instance, new JObject { ["pulseAgeMs"] = MainThreadPulse.AgeMs }), 2000);
                    }
                    Wake.WaitOne(100);
                }
            }
        }

        static void ReadLoop(ClientWebSocket ws)
        {
            while (ws.State == WebSocketState.Open && !_stop)
            {
                var msg = Receive(ws, Timeout.Infinite);
                if (msg == null) return;
                if ((string)msg["type"] == "command")
                    Command?.Invoke((string)msg["body"]?["id"], (string)msg["body"]?["action"]);
            }
        }

        static void Send(ClientWebSocket ws, string frame, int timeoutMs)
        {
            if (ws == null || ws.State != WebSocketState.Open) return;
            if (!Monitor.TryEnter(SendLock, timeoutMs)) return; // ClientWebSocket allows one send at a time
            try { ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(frame)), WebSocketMessageType.Text, true, CancellationToken.None).Wait(timeoutMs); }
            catch (Exception) { }
            finally { Monitor.Exit(SendLock); }
        }

        static JObject Receive(ClientWebSocket ws, int timeoutMs)
        {
            var buf = new byte[16 * 1024];
            var sb = new StringBuilder();
            try
            {
                while (true)
                {
                    var t = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                    if (!t.Wait(timeoutMs)) return null;
                    if (t.Result.MessageType == WebSocketMessageType.Close) return null;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, t.Result.Count));
                    if (t.Result.EndOfMessage) return JObject.Parse(sb.ToString());
                }
            }
            catch (Exception) { return null; }
        }
    }
}
