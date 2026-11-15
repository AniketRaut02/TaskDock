using System;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace TaskDock.Unity
{
    /// <summary>Protocol v1 envelope (protocol/taskdock.schema.json). Pure code, no Unity APIs.</summary>
    public static class Wire
    {
        public const int Version = 1;
        static long _seq;

        public static string Frame(string type, string instance, JObject body) => new JObject
        {
            ["v"] = Version,
            ["type"] = type,
            ["seq"] = Interlocked.Increment(ref _seq),
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["src"] = new JObject { ["adapter"] = "unity", ["instance"] = instance },
            ["body"] = body,
        }.ToString(Newtonsoft.Json.Formatting.None);

        public static JObject State(string state, string label, string detail, double? progress)
        {
            var b = new JObject { ["state"] = state, ["label"] = label };
            if (!string.IsNullOrEmpty(detail)) b["detail"] = detail;
            if (progress.HasValue) b["progress"] = Math.Round(Math.Max(0, Math.Min(1, progress.Value)), 3);
            return b;
        }

        /// <summary>Stable across reconnects and domain reloads: pid + hash of the project path.</summary>
        public static string Instance(int pid, string projectPath)
        {
            uint h = 2166136261; // FNV-1a
            foreach (var ch in NormalizePath(projectPath).ToLowerInvariant()) { h ^= ch; h *= 16777619; }
            return "unity:" + pid + ":" + (h & 0xFFFFFF).ToString("x6");
        }

        public static string NormalizePath(string p) => p.Replace('\\', '/').TrimEnd('/');

        /// <summary>%LOCALAPPDATA%\TaskDock\hub.json → (port, token), or null while the widget isn't running.</summary>
        public static (int Port, string Token)? ReadHub()
        {
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskDock", "hub.json");
                if (!File.Exists(path)) return null;
                var j = JObject.Parse(File.ReadAllText(path));
                return ((int)j["port"], (string)j["token"]);
            }
            catch (Exception) { return null; } // half-written or corrupt: try again next round
        }
    }

    /// <summary>Which single state to show when several things are true. Pure, so EditMode tests can cover it.</summary>
    public static class StateRules
    {
        public struct Flags
        {
            public bool Building, Compiling, Importing, Playing, Paused;
            public int CompileErrors; // 0 none, -1 failed with unknown count
            public string TaskName; public float TaskProgress; // a running UnityEditor.Progress item, or null
        }

        public static (string State, string Detail, double? Progress) Pick(Flags f)
        {
            var task = f.TaskName == null ? null : f.TaskName + (f.TaskProgress >= 0 ? $" {f.TaskProgress:P0}" : "");
            if (f.Building) return ("Building", task, null);
            if (f.Compiling) return ("Compiling", null, null);
            if (f.Importing) return ("Importing", task, null);
            if (f.Playing) return (f.Paused ? "Paused" : "Playing", task, null);
            if (f.CompileErrors != 0) return (f.CompileErrors > 0 ? $"Compile errors ({f.CompileErrors})" : "Compile errors", "Fix them to compile again", null);
            if (task != null) return ("Background task", task, f.TaskProgress >= 0 ? f.TaskProgress : (double?)null);
            return ("Idle", null, null);
        }
    }
}
