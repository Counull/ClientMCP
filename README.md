# ClientMCP

![ClientMCP](doc/images/clientmcp-banner.png)

面向 Unity 与 .NET 宿主的通用运行时诊断插件。AI 可通过外部脚本或 MCP 定位对象、查看成员、读写变量、构造对象和调用方法，在游戏持续运行时组合完成具体操作。

> 当前为 `0.1.0-preview.7`。插件提供反射元操作、可复用操作计划及可选中转连接，不包含消费项目的业务命令。实际验证范围见[当前状态](doc/status.md)；公网部署须单独验收。

通用核心不依赖 Unity；两端共享反射、对象句柄、操作计划和传输。另有默认关闭的 Roslyn C# 同步脚本模块，支持参数、句柄与操作编号回执。双端接入、可选模块安装及执行限制见[核心与宿主说明](doc/architecture-and-release.md#双端通用核心与-c-脚本)。

## 安装

### Unity

在 Unity Package Manager 中选择 **Install package from git URL**，输入：

```text
https://github.com/Counull/ClientMCP.git?path=/Project/Packages/com.counull.clientmcp#main
```

该地址跟随开发分支，目前没有稳定版本。开发期间也可通过 UPM 本地依赖使用包目录，避免维护两份源码。

### .NET

克隆完整仓库，在应用的 `.csproj` 中引用 `DotNet/ClientMcp.Core/ClientMcp.Core.csproj`。例如，应用工程目录与 `ClientMCP` 仓库目录并列时：

```xml
<ItemGroup>
  <ProjectReference Include="../ClientMCP/DotNet/ClientMcp.Core/ClientMcp.Core.csproj" />
</ItemGroup>
```

路径相对于应用的 `.csproj`，按实际目录调整。Core 目标为 **.NET Standard 2.0**，不依赖 Unity；它直接编译仓库 `Project/Packages/com.counull.clientmcp/Runtime/Core/` 的共享源码，所以应保留完整仓库结构，不能只复制 `DotNet/`。普通 .NET 接入无需运行 Unity 或 `Server/` 中转进程。

需要可选 C# 脚本时，再引用 `DotNet/ClientMcp.Scripting/ClientMcp.Scripting.csproj`，并在启动选项中显式设置 `ScriptCompiler = new RoslynScriptCompiler()`。仅使用反射和操作计划时只需 Core，默认不启用脚本。

## 使用

### Unity

Editor Play Mode：选择 **Tools → ClientMCP → Enable For Play Mode**，再进入 Play。构建后的程序通过命令行显式启用：

```text
<程序> --clientmcp --clientmcp-info <本地连接文件.json>
```

在 Unity 中打开 **Tools → ClientMCP → Connection Status**，可直接启停当前 Play 实例，查看监听地址、连接文件位置、MCP / 反射 HTTP 请求计数，以及最近 16 条请求的说明和结果。窗口读取不会产生网络请求。监听表示入口可用；是否实际调用过，以请求记录为准，不推断 AI 持续在线。

### .NET

Core 已提供 .NET Adapter。`RegisteredObjectHost` 注册可发现的根对象；执行方式按应用的线程模型选择：

| 执行入口 | 适用情况 | 调用方式 |
| --- | --- | --- |
| `ClientMcpHost` | 应用已有游戏循环或指定执行线程 | 在该线程创建、`Start`，每轮调用 `Pump`，退出时 `Dispose` |
| `ClientMcpThreadHost` | 对象可由专用诊断线程访问 | 自动创建线程、注册根对象并周期 `Pump`；等待 `Ready`，退出时 `Dispose` |

下面是可运行的 .NET 8 控制台示例，放入已引用 Core 的应用 `Program.cs`：

```csharp
using System;
using System.IO;
using System.Linq;
using ClientMcp;

// .NET 的启用开关由应用自己处理；仅引用 Core 不会自动启动监听。
if (!args.Contains("--clientmcp")) return;

var infoPath = Path.Combine(Path.GetTempPath(), $"clientmcp-demo-{Environment.ProcessId}.json");
using var diagnostics = new ClientMcpThreadHost(
    roots => roots.Register("app", new DemoRoot()),
    new ClientMcpOptions { InfoPath = infoPath });
diagnostics.Ready.GetAwaiter().GetResult();

Console.WriteLine($"ClientMCP info: {infoPath}");
Console.WriteLine("按回车结束示例并关闭诊断入口。");
Console.ReadLine();

public sealed class DemoRoot
{
    public int Value = 42;
}
```

在应用目录执行 `dotnet run -- --clientmcp`，然后在另一个终端使用下方的外部客户端连接打印出的文件。示例的根对象在诊断线程中创建并访问；集成到已有应用时，应让 `diagnostics` 随应用生命周期保留，结束时释放。

如果业务对象必须在已有游戏循环或 Actor 线程访问，使用下面的接入片段；`applicationRoot` 是现有根对象，`privateInfoPath` 是私有连接文件路径：

```csharp
var roots = new RegisteredObjectHost();
roots.Register("app", applicationRoot);
using var host = new ClientMcpHost(roots);
host.Start(new ClientMcpOptions { InfoPath = privateInfoPath });

// 放入同一线程的现有 Tick/Update，每轮调用；宿主退出时在该线程 Dispose。
host.Pump();
```

`ClientMcpHost` 的创建、启动、Pump 和释放须在同一宿主线程执行。专用线程 Adapter 不会自动切换到业务线程，也不会让对象变成线程安全；已有线程约束的入口应保留其调度方式。`.NET find` 使用 `scope="roots"` 查找显式注册的根对象，再沿成员获取其他对象，不扫描整个托管堆。

### 两端共用的外部客户端与 MCP

本机入口只监听 `127.0.0.1`，默认随机端口。连接文件包含 `endpoint`、随机 `token` 与 `sessionId`，应保存在本地私有目录，不提交或公开。Unity 的启动参数由插件处理；普通 .NET 的启用条件与 `ClientMcpOptions` 由应用接入代码设置。

下载仓库的 `Tools/clientmcp.py` 后，可直接使用 Python 标准库发送请求：

```sh
python Tools/clientmcp.py --info <本地连接文件.json> --health
# Unity：查找场景对象。
python Tools/clientmcp.py --info <本地连接文件.json> '{"op":"find","description":"查找当前场景的对象","type":"UnityEngine.GameObject"}'
# .NET：查找上面示例注册的根对象，返回名称 app 和当前对象句柄。
python Tools/clientmcp.py --info <本地连接文件.json> '{"op":"find","description":"查找示例根对象","type":"DemoRoot","scope":"roots"}'
```

Python 调用、对象句柄、属性与重载选择见 [外部脚本说明](Tools/README.md)。本机同一地址下的 `/mcp` 提供 JSON 响应模式的 MCP Streamable HTTP；请求需要连接文件中的 Bearer 令牌。`/reflect` 供脚本直接调用，两者使用同一个反射执行器。

每次反射操作必须携带 `description`，说明本次操作目的；说明去首尾空白后须为 1～200 字符且不含控制字符。Unity Console 在实际执行前记录说明，随后记录成功或失败；.NET 可通过宿主构造函数的 `log` 回调接入应用日志。两端状态接口均提供请求记录，只记录主动提供的说明，不自动展开参数或返回值。Python 方法可显式传 `description=`，省略时由客户端提供通用操作说明；原始 JSON 和 MCP 工具参数必须自行填写。

相同操作优先复用已准备的反射计划：首次可用 `plan execute` 一次准备并执行，后续 `run` 用新操作编号重新执行；原编号重试或查结果不会重复执行。`resultSteps` 可限制返回范围。每次执行从当前参数或前序结果取得运行对象，一组短步骤在同一宿主执行回调中顺序完成，失败停止、不自动回滚。使用规则及 Python 示例见[操作计划](Tools/README.md#优先复用已验证的操作计划)。Editor Play、Unity Player 与 .NET 宿主共用同一实现，不依赖临时编译代码。

Unity 的反射请求在主线程执行，.NET 则在所选 Adapter 的宿主线程执行。查看成员不会自动运行 getter，属性访问和方法调用必须显式指定。它是受信任的本机开发入口，访问能力等同于在当前进程执行代码；请求超时不保证终止已经开始执行的方法。

## 环境要求

- 包声明最低 Unity **2022.3**，开发工程使用 **6000.5.9f1**；实际验证范围见 [当前状态](doc/status.md)。
- `ClientMcp.Core` 项目目标为 **.NET Standard 2.0**，由兼容的 .NET 宿主引用；上方控制台示例使用 .NET 8。
- Git：需已安装并加入 PATH。
- 外部脚本使用 Python 3，无第三方 Python 依赖；Unity 使用官方 Newtonsoft Json 包，.NET 项目通过 NuGet 引用 Newtonsoft.Json。
- IL2CPP 的反射裁剪保留和其他平台须单独验证，不自动生成宿主业务类型的保留清单。

项目设计见 [远程诊断连接方案](doc/remote-diagnostics.md)。

## 通过中转连接多个实例

仓库 `Server/` 提供一个 ASP.NET Core 中转服务。Unity 与 .NET 宿主均可通过 `ClientMcpOptions.RelayUrl/RelayToken/InstanceName` 显式配置出站连接，主动通过 `/client` 建立 WSS 连接；Codex 等工具通过 `/mcp` 使用 HTTPS MCP。先用 `clientmcp_instances` 列出实例，再在每次反射或状态调用中传入 `instanceId`。本机入口继续可用，两种接入共用目标实例的宿主执行队列和计划缓存。普通 .NET 本机接入不需要部署中转。

中转的启动配置、客户端启用方式和断线语义见[连接说明](doc/remote-diagnostics.md)。默认不建立出站连接；开发者令牌可操作此中转接入的全部实例，按受信任诊断入口管理。

## 许可证

项目采用 [MIT](LICENSE) 许可证。第三方依赖遵守各自许可证。
