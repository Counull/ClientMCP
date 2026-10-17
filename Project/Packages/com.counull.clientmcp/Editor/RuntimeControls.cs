using UnityEditor;
using UnityEngine;

namespace ClientMcp.Editor
{
    /// <summary>仅负责开发者显式启用 Play 接入；真实操作仍由同一个 Runtime 执行。</summary>
    [InitializeOnLoad]
    internal static class RuntimeControls
    {
        const string EnabledKey = "ClientMCP.EnableForPlayMode";
        const string MenuPath = "Tools/ClientMCP/Enable For Play Mode";
        static RuntimeControls()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += ClientMcpRuntime.Stop;
            EditorApplication.delayCall += ResumeAfterReload;
        }

        [MenuItem(MenuPath)]
        static void Toggle()
        {
            bool enabled = !SessionState.GetBool(EnabledKey, false);
            SessionState.SetBool(EnabledKey, enabled);
            if (!Application.isPlaying) return;
            if (enabled) ClientMcpRuntime.Start();
            else ClientMcpRuntime.Stop();
        }

        [MenuItem(MenuPath, true)]
        static bool Validate() { Menu.SetChecked(MenuPath, SessionState.GetBool(EnabledKey, false)); return true; }

        static void ResumeAfterReload()
        {
            if (Application.isPlaying && SessionState.GetBool(EnabledKey, false)) ClientMcpRuntime.Start();
        }

        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(EnabledKey, false)) ClientMcpRuntime.Start();
            if (state == PlayModeStateChange.ExitingPlayMode) ClientMcpRuntime.Stop();
        }
    }
}
