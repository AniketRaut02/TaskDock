using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TaskDock.VisualStudio
{
    /// <summary>
    /// WebSocket to the TaskDock hub on a background thread: hello with the token from hub.json, heartbeats (so the hub
    /// can tell a frozen Visual Studio apart), a full snapshot after every reconnect, 1 s → 5 s backoff.
    /// Same protocol and behaviour as the Unity package's client.
    /// </summary>
    sealed class HubClient
    {
        readonly object _sendLock = new object();
        readonly ConcurrentQueue<string> _outbox = new ConcurrentQueue<string>();
        readonly AutoResetEvent _wake = new AutoResetEvent(false);
        readonly string _instance, _label;
        readonly JObject _hello;
        readonly Action<string> _log;
        readonly Thread _thread;
        volatile bool _stop;
        volatile ClientWebSocket _ws;
        volatile string _lastState; // resent after every reconnect: snapshots, never deltas
        string _lastBody;
        string _logged;
        long _seq;

        public HubClient(string instance, string label, JObject hello, Action<string> log)
        {
            _instance = instance;
            _label = label;
            _hello = hello;
            _log = log;
            _thread = new Thread(Run) { IsBackground = true, Name = "TaskDock hub client" };
            _thread.Start();
        }

        public void SendState(JObject body)
        {
            body["label"] = _label;
            var key = body.ToString(Newtonsoft.Json.Formatting.None);
            if (key == _lastBody) return; // unchanged
            _lastBody = key;
            var frame = Frame("state", body);
            _lastState = frame;
            _outbox.Enqueue(frame);
            _wake.Set();
        }

        public void Stop(string byeReason)
        {
            _stop = true;
            var ws = _ws;
            Send(ws, Frame("bye", new JObject { ["reason"] = byeReason }), 250);
            try { ws?.Abort(); } catch (Exception) { }
            _wake.Set();
        }

        string Frame(string type, JObject body) => new JObject
        {
            ["v"] = 1, ["type"] = type, ["seq"] = Interlocked.Increment(ref _seq), ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["src"] = new JObject { ["adapter"] = "visualstudio", ["instance"] = _instance }, ["body"] = body,
        }.ToString(Newtonsoft.Json.Formatting.None);

        void Note(string message)
        {
            if (message == _logged) return; // once per distinct message, not every retry
            _logged = message;
            _log(message);
        }

        void Run()
        {
            var backoff = 1000;
            while (!_stop)
            {
                try { Session(); backoff = 1000; }
                catch (Exception e) { Note("Can't reach the TaskDock widget: " + e.GetBaseException().Message + " Retrying."); }
                _ws = null;
                if (_stop) break;
                _wake.WaitOne(backoff);
                backoff = Math.Min(backoff * 2, 5000);
            }
        }

        void Session()
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskDock", "hub.json");
            if (!File.Exists(path)) { Note(path + " not found: is TaskDock running? Retrying."); return; }
            var hub = JObject.Parse(File.ReadAllText(path));
            var port = (int)hub["port"];

            using (var ws = new ClientWebSocket())
            {
                if (!ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), CancellationToken.None).Wait(3000)) { Note($"No answer on port {port}. Retrying."); return; }
                var hello = (JObject)_hello.DeepClone();
                hello["token"] = (string)hub["token"];
                Send(ws, Frame("hello", hello), 2000);
                var welcome = Receive(ws, 3000);
                if (welcome == null || (string)welcome["type"] != "welcome") { Note("The widget refused the hello (token changed?). Retrying."); return; }
                var heartbeatMs = (int?)welcome["body"]?["heartbeatMs"] ?? 5000;

                _ws = ws;
                Note($"Connected to the TaskDock widget on port {port}.");
                var last = _lastState;
                if (last != null) Send(ws, last, 2000); // full snapshot after every (re)connect
                while (_outbox.TryDequeue(out _)) { }   // anything queued while offline is older than that snapshot

                var reader = Task.Run(() => { while (ws.State == WebSocketState.Open && !_stop && Receive(ws, Timeout.Infinite) != null) { } });
                var beat = Stopwatch.StartNew();
                while (!_stop && ws.State == WebSocketState.Open && !reader.IsCompleted)
                {
                    while (_outbox.TryDequeue(out var frame)) Send(ws, frame, 2000);
                    if (beat.ElapsedMilliseconds >= heartbeatMs)
                    {
                        beat.Restart();
                        Send(ws, Frame("heartbeat", new JObject()), 2000);
                    }
                    _wake.WaitOne(100);
                }
                if (!_stop) Note("Lost the connection to the TaskDock widget. Reconnecting.");
            }
        }

        void Send(ClientWebSocket ws, string frame, int timeoutMs)
        {
            if (ws == null || ws.State != WebSocketState.Open) return;
            if (!Monitor.TryEnter(_sendLock, timeoutMs)) return; // one send at a time
            try { ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(frame)), WebSocketMessageType.Text, true, CancellationToken.None).Wait(timeoutMs); }
            catch (Exception) { }
            finally { Monitor.Exit(_sendLock); }
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
