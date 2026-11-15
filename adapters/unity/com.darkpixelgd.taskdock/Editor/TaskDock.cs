using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEngine;

namespace TaskDock.Unity
{
    /// <summary>Runs after every domain reload: reads hub.json (via HubClient), starts the client and the tracker.</summary>
    [InitializeOnLoad]
    static class TaskDockBootstrap
    {
        static TaskDockBootstrap()
        {
            // Unity 6 runs asset imports in worker editors that load this code too; only the real Editor reports.
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;
            if (TaskDockPreferences.Enabled) Start();
        }

        public static void Start()
        {
            var project = Wire.NormalizePath(Path.GetDirectoryName(Application.dataPath));
            var pid = Process.GetCurrentProcess().Id;
            var instance = Wire.Instance(pid, project);
            var hello = new JObject
            {
                ["adapter"] = "unity",
                ["instance"] = instance,
                ["version"] = "0.1.0",
                ["pid"] = pid,
                ["match"] = new JObject { ["projectPath"] = project },
                ["actions"] = new JArray("enterPlay", "exitPlay", "togglePause"),
            };

            MainThreadPulse.Start();
            CommandDispatcher.Start();
            EditorStateTracker.Start(Path.GetFileName(project));
            HubClient.Start(instance, hello, TaskDockPreferences.PortOverride);

            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
            EditorApplication.quitting += Quitting;
        }

        public static void Stop(string bye)
        {
            AssemblyReloadEvents.beforeAssemblyReload -= BeforeReload;
            EditorApplication.quitting -= Quitting;
            EditorStateTracker.Stop();
            CommandDispatcher.Stop();
            MainThreadPulse.Stop();
            HubClient.Stop(bye);
        }

        /// <summary>The last frame this domain can send: the hub keeps showing Reloading through the silence.</summary>
        static void BeforeReload()
        {
            HubClient.SendNow(Wire.State("Reloading", EditorStateTracker.Label, null, null));
            Stop(null);
        }

        static void Quitting() => Stop("editor quitting");
    }

    /// <summary>Records the last EditorApplication.update tick; its age rides on every heartbeat.</summary>
    static class MainThreadPulse
    {
        static long _last = Stopwatch.GetTimestamp();

        public static int AgeMs => (int)Math.Min(int.MaxValue, (Stopwatch.GetTimestamp() - Interlocked.Read(ref _last)) * 1000 / Stopwatch.Frequency);

        public static void Start()
        {
            Tick();
            EditorApplication.update += Tick;
        }

        public static void Stop() => EditorApplication.update -= Tick;

        static void Tick() => Interlocked.Exchange(ref _last, Stopwatch.GetTimestamp());
    }

    /// <summary>Hub commands arrive on the socket thread; Editor APIs only run on the main thread, from update.</summary>
    static class CommandDispatcher
    {
        static readonly ConcurrentQueue<(string Id, string Action)> Queue = new ConcurrentQueue<(string, string)>();

        public static void Start()
        {
            HubClient.Command += Enqueue;
            EditorApplication.update += Drain;
        }

        public static void Stop()
        {
            HubClient.Command -= Enqueue;
            EditorApplication.update -= Drain;
        }

        static void Enqueue(string id, string action) => Queue.Enqueue((id, action));

        static void Drain()
        {
            while (Queue.TryDequeue(out var c))
            {
                string error = null;
                switch (c.Action)
                {
                    case "enterPlay": if (!EditorApplication.isPlaying) EditorApplication.EnterPlaymode(); break;
                    case "exitPlay": if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode(); break;
                    case "togglePause": if (EditorApplication.isPlaying) EditorApplication.isPaused = !EditorApplication.isPaused; else error = "not playing"; break;
                    default: error = "unknown action " + c.Action; break;
                }
                HubClient.SendResult(c.Id, error == null, error);
            }
        }
    }

    /// <summary>Subscribes to Editor events, derives the current state and sends a snapshot on every change.</summary>
    static class EditorStateTracker
    {
        static bool _compiling, _building;
        static int _errors, _errorsThisCompile;
        static string _sent;
        static double _nextPoll;

        public static string Label { get; private set; } = "Unity";

        public static void Start(string label)
        {
            Label = label;
            // Errors survive until the next successful compile; a fresh domain only knows "failed", not how many.
            _errors = EditorUtility.scriptCompilationFailed ? -1 : 0;
            CompilationPipeline.compilationStarted += OnCompileStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            CompilationPipeline.compilationFinished += OnCompileFinished;
            EditorApplication.playModeStateChanged += OnPlayMode;
            EditorApplication.pauseStateChanged += OnPause;
            EditorApplication.update += Poll;
            Push();
        }

        public static void Stop()
        {
            CompilationPipeline.compilationStarted -= OnCompileStarted;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompiled;
            CompilationPipeline.compilationFinished -= OnCompileFinished;
            EditorApplication.playModeStateChanged -= OnPlayMode;
            EditorApplication.pauseStateChanged -= OnPause;
            EditorApplication.update -= Poll;
        }

        static void OnCompileStarted(object _) { _compiling = true; _errorsThisCompile = 0; Push(); }
        static void OnAssemblyCompiled(string _, CompilerMessage[] messages) => _errorsThisCompile += messages.Count(m => m.type == CompilerMessageType.Error);
        static void OnCompileFinished(object _) { _compiling = false; _errors = _errorsThisCompile; Push(); }
        static void OnPlayMode(PlayModeStateChange _) => Push();
        static void OnPause(PauseState _) => Push();

        /// <summary>Called from a build callback while the main thread is busy building.</summary>
        public static void SetBuilding(bool building) { _building = building; Push(); }

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + 0.25;
            if (_building && !BuildPipeline.isBuildingPlayer) _building = false; // a failed build skips the post-process callback
            Push();
        }

        static void Push()
        {
            var f = new StateRules.Flags
            {
                Building = _building || BuildPipeline.isBuildingPlayer,
                Compiling = _compiling || EditorApplication.isCompiling,
                Importing = EditorApplication.isUpdating,
                Playing = EditorApplication.isPlaying,
                Paused = EditorApplication.isPaused,
                CompileErrors = _errors,
            };
            if (TaskDockPreferences.ShowBackgroundDetail)
            {
                var item = Progress.EnumerateItems().FirstOrDefault(i => i.running);
                if (item != null) { f.TaskName = item.name; f.TaskProgress = item.progress; }
            }
            var (state, detail, progress) = StateRules.Pick(f);
            var body = Wire.State(state, Label, detail, progress);
            var key = body.ToString(Newtonsoft.Json.Formatting.None);
            if (key == _sent) return;
            _sent = key;
            HubClient.SendState(body);
        }
    }

    /// <summary>Building blocks the main thread, so these callbacks send the state directly.</summary>
    sealed class TaskDockBuildHooks : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder => int.MinValue;
        public void OnPreprocessBuild(BuildReport report) => EditorStateTracker.SetBuilding(true);
        public void OnPostprocessBuild(BuildReport report) => EditorStateTracker.SetBuilding(false);
    }

    /// <summary>Preferences → TaskDock (EditorPrefs, per machine).</summary>
    static class TaskDockPreferences
    {
        const string EnabledKey = "TaskDock.Enabled", PortKey = "TaskDock.PortOverride", DetailKey = "TaskDock.ShowBackgroundDetail";

        public static bool Enabled => EditorPrefs.GetBool(EnabledKey, true);
        public static int PortOverride => EditorPrefs.GetInt(PortKey, 0);
        public static bool ShowBackgroundDetail => EditorPrefs.GetBool(DetailKey, true);

        [SettingsProvider]
        static SettingsProvider Create() => new SettingsProvider("Preferences/TaskDock", SettingsScope.User)
        {
            keywords = new[] { "TaskDock", "widget", "hub" },
            guiHandler = _ =>
            {
                EditorGUILayout.LabelField("Status", HubClient.Connected ? "Connected to the TaskDock widget" : "Not connected (is TaskDock running?)");
                EditorGUI.BeginChangeCheck();
                var enabled = EditorGUILayout.Toggle("Enabled", Enabled);
                var port = EditorGUILayout.IntField(new GUIContent("Port override", "0 = read the port from hub.json"), PortOverride);
                var detail = EditorGUILayout.Toggle(new GUIContent("Show background tasks", "Bakes, async shader compiles and other Progress items"), ShowBackgroundDetail);
                if (!EditorGUI.EndChangeCheck()) return;

                var restart = enabled != Enabled || port != PortOverride;
                EditorPrefs.SetBool(EnabledKey, enabled);
                EditorPrefs.SetInt(PortKey, Math.Max(0, port));
                EditorPrefs.SetBool(DetailKey, detail);
                if (!restart) return;
                TaskDockBootstrap.Stop(enabled ? null : "disabled in preferences");
                if (enabled) TaskDockBootstrap.Start();
            },
        };
    }
}
