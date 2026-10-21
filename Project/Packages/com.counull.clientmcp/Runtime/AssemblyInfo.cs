using System.Reflection;

[assembly: AssemblyDescription("ClientMCP authenticated runtime reflection primitives.")]

// 抽取核心后保留既有预编译使用方的类型解析。
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(ClientMcp.ClientMcpOptions))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(ClientMcp.ReflectionSession))]
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(ClientMcp.ReflectionPlanSession))]
