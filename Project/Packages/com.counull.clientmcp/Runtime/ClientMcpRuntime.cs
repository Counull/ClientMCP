using System;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ClientMcp
{
    [DefaultExecutionOrder(-32000)]
    public sealed class ClientMcpRuntime : MonoBehaviour
    {
        static ClientMcpRuntime current;
        ClientMcpHost host;
        public static bool IsRunning => current != null && current.host != null && current.host.IsRunning;
        public static JObject GetConnectionStatus() => current == null || current.host == null
            ? ClientMcpHost.EmptyConnectionStatus() : current.host.GetConnectionStatus();
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetRuntime() { if (current != null) current.host?.Dispose(); current = null; }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void StartFromCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            if (Array.IndexOf(args, "--clientmcp") < 0) return;
            var options = new ClientMcpOptions
            {
                RelayUrl = Environment.GetEnvironmentVariable("CLIENTMCP_RELAY_URL"),
                RelayToken = Environment.GetEnvironmentVariable("CLIENTMCP_RELAY_TOKEN"),
                InstanceName = Environment.GetEnvironmentVariable("CLIENTMCP_INSTANCE_NAME")
            };
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--clientmcp-info") options.InfoPath = args[i + 1];
                if (args[i] == "--clientmcp-port") options.Port = int.Parse(args[i + 1]);
            }
            if (Array.IndexOf(args, "--clientmcp-scripts") >= 0)
            {
#if ENABLE_IL2CPP
                throw new PlatformNotSupportedException("C# 脚本执行不支持 IL2CPP。");
#else
                var compiler = Type.GetType("ClientMcp.RoslynScriptCompiler, ClientMcp.Scripting", true);
                options.ScriptCompiler = (IScriptCompiler)Activator.CreateInstance(compiler);
#endif
            }
            Start(options);
        }
        public static void Start(ClientMcpOptions options = null)
        {
            if (!Application.isPlaying) throw new InvalidOperationException("ClientMCP requires a running instance.");
            if (IsRunning) return;
#if ENABLE_IL2CPP
            if (options?.ScriptCompiler != null) throw new PlatformNotSupportedException("C# 脚本执行不支持 IL2CPP。");
#endif
            var gameObject = new GameObject("ClientMCP Runtime");
            DontDestroyOnLoad(gameObject);
            var runtime = gameObject.AddComponent<ClientMcpRuntime>();
            current = runtime;
            runtime.host = new ClientMcpHost(new UnityReflectionHost(), () => new JObject
            {
                ["hostKind"] = "unity", ["name"] = Application.productName,
                ["frameCount"] = Time.frameCount, ["unityVersion"] = Application.unityVersion,
                ["isEditor"] = Application.isEditor, ["isPlaying"] = Application.isPlaying
            }, (message, warning) => { if (warning) Debug.LogWarning(message); else Debug.Log(message); });
            try { runtime.host.Start(options); }
            catch { runtime.host.Dispose(); Destroy(gameObject); current = null; throw; }
        }
        public static void Stop()
        {
            if (current == null) return;
            current.host?.Dispose(); Destroy(current.gameObject); current = null;
        }
        public static void ConnectRelay(string url, string token, string instanceName = null)
        {
            if (!IsRunning) throw new InvalidOperationException("Start ClientMCP before connecting a relay.");
            current.host.ConnectRelay(url, token, instanceName);
        }
        public static void DisconnectRelay() { if (current != null) current.host?.DisconnectRelay(); }
        void Update() => host?.Pump();
        void OnDestroy() { host?.Dispose(); if (current == this) current = null; }
        void OnApplicationQuit() => host?.Dispose();
    }
}
