using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using Task = System.Threading.Tasks.Task;

namespace TaskDock.VisualStudio
{
    /// <summary>
    /// Reports this Visual Studio window to the TaskDock widget: builds (with failed project counts) and debugging
    /// (with "Paused" when a breakpoint is hit, which needs you). Loads in the background, so it never slows startup.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid("59e46406-72ad-4fd9-ae70-8b2a051f69e5")]
    [ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.FolderOpened_string, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class TaskDockPackage : AsyncPackage, IVsUpdateSolutionEvents2, IVsDebuggerEvents, IVsSolutionEvents, IVsSolutionEvents7
    {
        readonly BuildDebugState _state = new BuildDebugState();
        IVsSolution _solution;
        IVsSolutionBuildManager2 _build;
        IVsDebugger _debugger;
        uint _buildCookie, _debugCookie, _solutionCookie;
        HubClient _hub;
        string _solutionPath;

        protected override async Task InitializeAsync(CancellationToken cancel, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancel);
            _solution = await GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
            _build = await GetServiceAsync(typeof(SVsSolutionBuildManager)) as IVsSolutionBuildManager2;
            _debugger = await GetServiceAsync(typeof(SVsShellDebugger)) as IVsDebugger;
            _solution?.AdviseSolutionEvents(this, out _solutionCookie);
            _build?.AdviseUpdateSolutionEvents(this, out _buildCookie);
            _debugger?.AdviseDebuggerEvents(this, out _debugCookie);
            Connect();
        }

        /// <summary>(Re)connects with the current solution as this tile's identity: a new solution is a new hello.</summary>
        void Connect()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string dir = null, file = null;
            _solution?.GetSolutionInfo(out dir, out file, out _);
            var path = !string.IsNullOrEmpty(file) ? file : !string.IsNullOrEmpty(dir) ? dir.TrimEnd('\\', '/') : null;
            if (_hub != null && path == _solutionPath) return;
            _solutionPath = path;
            _hub?.Stop("solution changed");

            var label = path == null ? "Visual Studio" : Path.GetFileNameWithoutExtension(path);
            var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            var instance = "visualstudio:" + pid + (path == null ? "" : ":" + Hash(path));
            var match = new JObject();
            if (path != null) match["solutionPath"] = path.Replace('\\', '/');
            _hub = new HubClient(instance, label, new JObject
            {
                ["adapter"] = "visualstudio", ["instance"] = instance, ["version"] = "0.1.0", ["pid"] = pid,
                ["match"] = match, ["actions"] = new JArray(),
            }, Log);
            Push();
        }

        void Push() => _hub?.SendState(_state.Snapshot());

        static string Hash(string s)
        {
            uint h = 2166136261; // FNV-1a, same as the other adapters
            foreach (var ch in s.Replace('\\', '/').TrimEnd('/').ToLowerInvariant()) { h ^= ch; h *= 16777619; }
            return (h & 0xFFFFFF).ToString("x6");
        }

        /// <summary>Connection messages go to an Output window pane called "TaskDock", once per distinct message.</summary>
        void Log(string message) => JoinableTaskFactory.RunAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            if (!(await GetServiceAsync(typeof(SVsOutputWindow)) is IVsOutputWindow output)) return;
            var id = new Guid("8c2f63a2-7d5e-4b0c-9a51-3f1e2b6c4d70");
            output.CreatePane(ref id, "TaskDock", 1, 1);
            output.GetPane(ref id, out var pane);
            pane?.OutputStringThreadSafe("[TaskDock] " + message + Environment.NewLine);
        }).FileAndForget("TaskDock/Log");

        // ---------- build ----------

        public int UpdateSolution_Begin(ref int pfCancelUpdate) { _state.BuildStarted(); Push(); return VSConstants.S_OK; }
        public int UpdateSolution_Done(int fSucceeded, int fModified, int fCancelCommand) { _state.BuildDone(fSucceeded != 0, fCancelCommand != 0); Push(); return VSConstants.S_OK; }
        public int UpdateSolution_StartUpdate(ref int pfCancelUpdate) => VSConstants.S_OK;
        public int UpdateSolution_Cancel() { _state.BuildDone(false, canceled: true); Push(); return VSConstants.S_OK; }
        public int OnActiveProjectCfgChange(IVsHierarchy pIVsHierarchy) => VSConstants.S_OK;
        public int UpdateProjectCfg_Begin(IVsHierarchy pHierProj, IVsCfg pCfgProj, IVsCfg pCfgSln, uint dwAction, ref int pfCancel) => VSConstants.S_OK;
        public int UpdateProjectCfg_Done(IVsHierarchy pHierProj, IVsCfg pCfgProj, IVsCfg pCfgSln, uint dwAction, int fSuccess, int fCancel)
        {
            _state.ProjectDone(fSuccess != 0);
            Push();
            return VSConstants.S_OK;
        }

        // ---------- debugging ----------

        public int OnModeChange(DBGMODE dbgmodeNew)
        {
            _state.DebugMode(dbgmodeNew & ~DBGMODE.DBGMODE_EncMask);
            Push();
            return VSConstants.S_OK;
        }

        // ---------- solution / folder identity ----------

        public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution) { Connect(); return VSConstants.S_OK; }
        public int OnAfterCloseSolution(object pUnkReserved) { Connect(); return VSConstants.S_OK; }
        public void OnAfterOpenFolder(string folderPath) => Connect();
        public void OnAfterCloseFolder(string folderPath) => Connect();
        public void OnQueryCloseFolder(string folderPath, ref int pfCancel) { }
        public void OnBeforeCloseFolder(string folderPath) { }
        public void OnAfterLoadAllDeferredProjects() { }
        public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;
        public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
        public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;
        public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;
        public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;
        public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;
        public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;
        public int OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_buildCookie != 0) _build?.UnadviseUpdateSolutionEvents(_buildCookie);
                if (_debugCookie != 0) _debugger?.UnadviseDebuggerEvents(_debugCookie);
                if (_solutionCookie != 0) _solution?.UnadviseSolutionEvents(_solutionCookie);
                _hub?.Stop("Visual Studio closing");
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// One tile state from build and debug events. Pure, no VS types beyond the debug mode enum.
    /// Priority: Paused (needs you) > Building > Debugging > last build result > Idle.
    /// A build result stays until the next build or debug session starts.
    /// </summary>
    sealed class BuildDebugState
    {
        bool _building, _debugging, _paused;
        int _projectsDone, _projectsFailed;
        DateTime _buildStart;
        string _result, _resultDetail;

        public void BuildStarted()
        {
            _building = true;
            _projectsDone = _projectsFailed = 0;
            _buildStart = DateTime.UtcNow;
            _result = null;
        }

        public void ProjectDone(bool ok)
        {
            _projectsDone++;
            if (!ok) _projectsFailed++;
        }

        public void BuildDone(bool succeeded, bool canceled)
        {
            if (!_building) return;
            _building = false;
            if (canceled) { _result = null; return; }
            var secs = (DateTime.UtcNow - _buildStart).TotalSeconds;
            var projects = _projectsDone == 1 ? "1 project" : $"{_projectsDone} projects";
            if (!succeeded || _projectsFailed > 0)
            {
                _result = _projectsFailed > 0 ? $"Build failed ({_projectsFailed} of {projects})" : "Build failed";
                _resultDetail = "See the Error List";
            }
            else
            {
                _result = "Build succeeded";
                _resultDetail = $"{projects} in {secs:0.#} s";
            }
        }

        public void DebugMode(DBGMODE mode)
        {
            var wasDebugging = _debugging;
            _debugging = mode != DBGMODE.DBGMODE_Design;
            _paused = mode == DBGMODE.DBGMODE_Break;
            if (_debugging && !wasDebugging) _result = null; // a new session: the last build result is old news
        }

        public JObject Snapshot()
        {
            string state, detail = null;
            if (_paused) { state = "Paused"; detail = "Stopped at a breakpoint or exception"; }
            else if (_building) { state = "Building"; detail = _projectsDone == 0 ? null : $"{_projectsDone} done" + (_projectsFailed > 0 ? $", {_projectsFailed} failed" : ""); }
            else if (_debugging) state = "Debugging";
            else if (_result != null) { state = _result; detail = _resultDetail; }
            else state = "Idle";
            var body = new JObject { ["state"] = state };
            if (detail != null) body["detail"] = detail;
            return body;
        }
    }
}
