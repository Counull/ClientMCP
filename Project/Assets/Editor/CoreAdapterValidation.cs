using System;
using System.Threading.Tasks;
using ClientMcp;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ClientMcp.Development
{
    public static class CoreAdapterValidation
    {
        public static void RunChecks()
        {
            RuntimeValidation.RunChecks();
            PlanValidation.RunChecks();
            var compilerType = Type.GetType("ClientMcp.RoslynScriptCompiler, ClientMcp.Scripting", true);
            var compiler = (IScriptCompiler)Activator.CreateInstance(compilerType);
            // 编译在后台，不执行用户脚本；委托在当前 Unity 线程才真正落地。
            var compiled = Task.Run(() => compiler.Compile("var go = new UnityEngine.GameObject(\"ScriptAdapterProbe\"); return go;")).GetAwaiter().GetResult();
            var reflection = new ReflectionSession(new UnityReflectionHost());
            var value = (GameObject)compiled(new ScriptGlobals(reflection, new JObject()));
            try
            {
                if (value == null || value.name != "ScriptAdapterProbe") throw new Exception("Unity 脚本未创建目标对象。");
                var handle = (string)reflection.Encode(value)["value"]["$ref"];
                if (!ReferenceEquals(reflection.GetObject(handle), value)) throw new Exception("脚本句柄未共用。");
                Debug.Log("CLIENTMCP_UNITY_SCRIPT_CHECKS_OK");
            }
            finally { if (value != null) UnityEngine.Object.DestroyImmediate(value); reflection.Clear(); }
        }
    }
}
