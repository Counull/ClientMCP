using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ClientMcp
{
    /// <summary>受信任同步脚本；使用 Roslyn 编译和 Assembly.Load，兼容 Mono 与普通 .NET。</summary>
    public sealed class RoslynScriptCompiler : IScriptCompiler
    {
        readonly object gate = new object();
        readonly Dictionary<string, Func<ScriptGlobals, object>> compiled = new Dictionary<string, Func<ScriptGlobals, object>>(StringComparer.Ordinal);
        public Func<ScriptGlobals, object> Compile(string code)
        {
            if (string.IsNullOrWhiteSpace(code) || code.Length > 65536) throw new ArgumentException("脚本须为 1 至 65536 字符。");
            lock (gate)
            {
                if (compiled.TryGetValue(code, out var previous)) return previous;
                if (compiled.Count >= 128) throw new InvalidOperationException("编译器已加载 128 份不同脚本；为限制不可卸载程序集，须重启宿主后再编译新代码。");
                var result = CompileCore(code);
                compiled.Add(code, result);
                return result;
            }
        }
        static Func<ScriptGlobals, object> CompileCore(string code)
        {
            var syntax = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(kind: SourceCodeKind.Script));
            if (syntax.GetRoot().DescendantNodes().Any(node => node is AwaitExpressionSyntax || node.ChildTokens().Any(token => token.IsKind(SyntaxKind.AsyncKeyword))))
                throw new NotSupportedException("脚本仅支持同步短操作；await/async 不受支持，异步工作请调用宿主已有的非阻塞入口。");
            var references = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic && HasLocation(assembly))
                .Select(assembly => assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.CreateScriptCompilation("ClientMcpScript_" + Guid.NewGuid().ToString("N"), syntax, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    usings: new[] { "System", "System.Linq", "System.Collections.Generic", "ClientMcp", "Newtonsoft.Json.Linq" }),
                returnType: typeof(object), globalsType: typeof(ScriptGlobals));
            using (var bytes = new MemoryStream())
            {
                var emitted = compilation.Emit(bytes);
                if (!emitted.Success)
                    throw new ArgumentException("脚本编译失败：" + string.Join("\n", emitted.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error).Select(item => item.ToString())));
                // Roslyn Scripting 的 netstandard loader 在 Unity Mono 上使用未实现的 AssemblyLoadContext。
                // 直接加载本次内存程序集，不依赖该 loader，也不产生临时源码文件。
                var assembly = Assembly.Load(bytes.ToArray());
                var type = assembly.GetType(compilation.ScriptClass.MetadataName, true);
                var factory = type.GetMethod("<Factory>", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var runner = (Func<object[], Task<object>>)Delegate.CreateDelegate(typeof(Func<object[], Task<object>>), factory);
                return globals =>
                {
                    // Roslyn submission slot 0 为 globals，slot 1 为本次 submission；每次执行全新状态。
                    var execution = runner(new object[] { globals, null });
                    if (!execution.IsCompleted) throw new NotSupportedException("脚本不得异步挂起。");
                    var result = execution.GetAwaiter().GetResult();
                    if (result is Task) throw new NotSupportedException("脚本返回 Task 不表示工作完成，请改为显式返回同步值。");
                    return result;
                };
            }
        }
        static bool HasLocation(Assembly assembly)
        {
            try { return !string.IsNullOrEmpty(assembly.Location) && File.Exists(assembly.Location); }
            catch (NotSupportedException) { return false; }
        }
    }
}
