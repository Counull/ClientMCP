using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace ClientMcp.Editor
{
    /// <summary>
    /// 直接查看当前 Editor Play 实例的接入状态；不发送 HTTP，不把查看窗口计为外部访问。
    /// 网络按请求通信，因此只展示监听和真实请求记录，不推断 AI 是否持续在线。
    /// </summary>
    internal sealed class ConnectionStatusWindow : EditorWindow
    {
        Vector2 scrollPosition;
        string startupError;

        [MenuItem("Tools/ClientMCP/Connection Status", priority = 0)]
        internal static void Open()
        {
            var window = GetWindow<ConnectionStatusWindow>("ClientMCP");
            window.minSize = new Vector2(520, 520);
            window.Show();
        }

        // Editor 自身以较低频率刷新检查窗口，无须在游戏每帧轮询或执行反射。
        void OnInspectorUpdate() => Repaint();

        void OnGUI()
        {
            var status = ClientMcpRuntime.GetConnectionStatus();
            bool running = (bool)status["isRunning"];
            int active = (int)status["activeRequests"];
            long requestCount = (long)status["mcpRequests"] + (long)status["reflectRequests"] + (long)status["healthRequests"];

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("ClientMCP 连接状态", EditorStyles.boldLabel);
            string state = !running ? "未启动" : active > 0 ? "正在处理请求" : requestCount == 0 ? "正在监听，等待首次请求" : "正在监听，当前空闲";
            EditorGUILayout.HelpBox(state, running ? MessageType.Info : MessageType.None);

            EditorGUI.BeginChangeCheck();
            bool enabled = EditorGUILayout.ToggleLeft("进入 Play 时启用 ClientMCP（仅本次 Editor 会话）", RuntimeControls.EnabledForPlayMode);
            if (EditorGUI.EndChangeCheck()) ChangeEnabled(enabled);

            using (new EditorGUI.DisabledScope(!Application.isPlaying || EditorApplication.isCompiling))
            {
                if (GUILayout.Button(running ? "停止接入" : "启动接入")) ChangeEnabled(!running);
            }
            if (!Application.isPlaying)
                EditorGUILayout.HelpBox("勾选启用后进入 Play，才能检查和操作游戏运行时。", MessageType.None);
            if (!string.IsNullOrEmpty(startupError)) EditorGUILayout.HelpBox(startupError, MessageType.Error);
            if (!running) return;

            EditorGUILayout.Space();
            string endpoint = (string)status["endpoint"];
            string infoPath = (string)status["infoPath"];
            SelectableRow("MCP 地址", endpoint + "/mcp");
            SelectableRow("连接文件", infoPath);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("复制 MCP 地址")) EditorGUIUtility.systemCopyBuffer = endpoint + "/mcp";
                if (GUILayout.Button("复制连接文件路径")) EditorGUIUtility.systemCopyBuffer = infoPath;
                using (new EditorGUI.DisabledScope(!File.Exists(infoPath)))
                    if (GUILayout.Button("定位连接文件")) EditorUtility.RevealInFinder(infoPath);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("MCP 请求", status["mcpRequests"].ToString());
            EditorGUILayout.LabelField("反射 HTTP 请求", status["reflectRequests"].ToString());
            EditorGUILayout.LabelField("健康检查", status["healthRequests"].ToString());
            EditorGUILayout.LabelField("处理 / 排队", active + " / " + status["pendingRequests"]);
            EditorGUILayout.LabelField("最近收到请求", FormatTime((string)status["lastRequestUtc"]));
            EditorGUILayout.HelpBox("监听状态不代表 AI 持续在线。下方仅记录已通过认证的实际请求；查看窗口不会增加计数。令牌保存在连接文件中，不在此显示。", MessageType.None);

            EditorGUILayout.LabelField("最近完成的请求（最多 16 条）", EditorStyles.boldLabel);
            var requests = (JArray)status["recentRequests"];
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            for (int index = requests.Count - 1; index >= 0; index--)
            {
                var request = requests[index];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField((string)request["description"] ?? "未提供操作说明", EditorStyles.wordWrappedLabel);
                    EditorGUILayout.LabelField(FormatTime((string)request["timeUtc"]) + "   " + request["path"] + "   " + request["operation"] + "   " + ((bool)request["success"] ? "成功" : "失败"), EditorStyles.miniLabel);
                }
            }
            EditorGUILayout.EndScrollView();
        }

        void ChangeEnabled(bool enabled)
        {
            startupError = null;
            try { RuntimeControls.SetEnabled(enabled); }
            catch (Exception error) { startupError = error.Message; }
            Repaint();
        }

        static void SelectableRow(string label, string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(80));
                EditorGUILayout.SelectableLabel(value, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
        }

        static string FormatTime(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time.ToLocalTime().ToString("HH:mm:ss.fff") : "尚无";
    }
}
