# ClientMCP

![ClientMCP](doc/images/clientmcp-banner.png)

面向 Unity 运行时的通用反射插件。AI 可通过外部脚本或 MCP 定位对象、查看成员、读写变量、构造对象和调用方法，在游戏持续运行时组合完成具体操作。

> 当前为 `0.1.0-preview.2`。插件只提供反射元操作，不包含任何消费项目的业务命令。首版接入限定本机；公网连接仍为后续设计。

## 安装

在 Unity Package Manager 中选择 **Install package from git URL**，输入：

```text
https://github.com/Counull/ClientMCP.git?path=/Project/Packages/com.counull.clientmcp#main
```

该地址跟随开发分支，目前没有稳定版本。开发期间也可通过 UPM 本地依赖使用包目录，避免维护两份源码。

## 使用

Editor Play Mode：选择 **Tools → ClientMCP → Enable For Play Mode**，再进入 Play。构建后的程序通过命令行显式启用：

```text
<程序> --clientmcp --clientmcp-info <本地连接文件.json>
```

插件只监听 `127.0.0.1`，默认随机端口。连接文件包含地址、随机令牌与运行会话标识，应保存在本地私有目录；不要提交或公开。没有显式启用时，不创建监听入口。

下载仓库的 `Tools/clientmcp.py` 后，可直接使用 Python 标准库发送请求：

```sh
python Tools/clientmcp.py --info <本地连接文件.json> --health
python Tools/clientmcp.py --info <本地连接文件.json> '{"op":"find","type":"UnityEngine.GameObject"}'
```

Python 调用、对象句柄、属性与重载选择见 [外部脚本说明](Tools/README.md)。本机同一地址下的 `/mcp` 提供 JSON 响应模式的 MCP Streamable HTTP；请求需要连接文件中的 Bearer 令牌。`/reflect` 供脚本直接调用，两者使用同一个反射执行器。

反射请求在 Unity 主线程执行。查看成员不会自动运行 getter，属性访问和方法调用必须显式指定。它是受信任的本机开发入口，访问能力等同于在当前进程执行代码；请求超时不保证终止已经开始执行的方法。

## 环境要求

- 包声明最低 Unity **2022.3**，开发工程使用 **6000.5.9f1**；实际验证范围见 [当前状态](doc/status.md)。
- Git：需已安装并加入 PATH。
- 外部脚本使用 Python 3，无第三方 Python 依赖；JSON 使用 Unity 官方 Newtonsoft Json 包。
- IL2CPP 的反射裁剪保留和其他平台须单独验证，不自动生成宿主业务类型的保留清单。

项目设计见 [远程诊断连接方案](doc/remote-diagnostics.md)。

## 许可证

项目采用 [MIT](LICENSE) 许可证。第三方依赖遵守各自许可证。
