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
        internal static bool EnabledForPlayMode => SessionState.GetBool(EnabledKey, false);
        static RuntimeControls()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += ClientMcpRuntime.Stop;
            EditorApplication.delayCall += ResumeAfterReload;
        }

        [MenuItem(MenuPath)]
        static void Toggle() => SetEnabled(!EnabledForPlayMode);

        // 菜单和状态窗口共用开关；Play 中立即生效，编辑状态只记录本次 Editor 会话的选择。
        internal static void SetEnabled(bool enabled)
        {
            SessionState.SetBool(EnabledKey, enabled);
            if (!Application.isPlaying) return;
            if (enabled) ClientMcpRuntime.Start();
            else ClientMcpRuntime.Stop();
        }

        [MenuItem(MenuPath, true)]
        static bool Validate() { Menu.SetChecked(MenuPath, EnabledForPlayMode); return true; }

        static void ResumeAfterReload()
        {
            if (Application.isPlaying && EnabledForPlayMode) ClientMcpRuntime.Start();
        }

        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode && EnabledForPlayMode) ClientMcpRuntime.Start();
            if (state == PlayModeStateChange.ExitingPlayMode) ClientMcpRuntime.Stop();
        }
    }
}
