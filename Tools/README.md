# 外部脚本客户端

`clientmcp.py` 只依赖 Python 3 标准库，无需安装 SDK。脚本通过本机 HTTP 接口发送通用反射操作，可以在 Editor 的 Play Mode 或支持的独立运行程序中使用。

## 连接一个运行实例

运行时必须显式启用 ClientMCP。通过启动参数启用时，建议同时用 `--clientmcp-info <绝对路径>` 指定私有连接文件：

```text
<程序> --clientmcp --clientmcp-info <私有连接文件路径>
```

也可以在消费工程中调用 `ClientMcpRuntime.Start(options)`；具体启用代码见包的使用说明。Editor 中需要进入 Play Mode，反射操作在游戏继续运行时执行。

运行时只监听本机地址，自动分配端口并生成随机 token。连接文件包含 `endpoint`、`token`、`sessionId`，应放在版本控制以外的私有位置。不指定路径时，文件为系统临时目录中的 `clientmcp-<进程ID>.json`。客户端要求明确选择文件，不会猜测或自动选择其他实例。

```powershell
python Tools/clientmcp.py --info <私有连接文件路径> --health
python Tools/clientmcp.py --info <私有连接文件路径> '{"op":"types","query":"UnityEngine.Time","limit":10}'
'{"op":"get","type":"UnityEngine.Time","member":"timeScale","kind":"property"}' |
    python Tools/clientmcp.py --info <私有连接文件路径> -
```

还可以显式传入 `--endpoint http://127.0.0.1:<端口> --token <token>`。日常使用连接文件，避免把 token 留在命令历史中。`--timeout` 设置请求超时秒数，默认 10 秒。无参数运行显示帮助；成功输出 `result` JSON，失败在标准错误输出原因并返回退出码 1。

## 在 Python 中组合操作

把 `Tools` 加入脚本模块搜索路径，或把脚本保存在 `clientmcp.py` 同目录，即可直接导入：

```python
from clientmcp import ClientMcp

client = ClientMcp(info="<私有连接文件路径>")
print(client.health())
print(client.types("UnityEngine.Time"))
print(client.inspect(type="UnityEngine.Time"))

# 默认只读字段；属性需要显式 kind="property"，因为 getter 可能执行代码。
before = client.get(type="UnityEngine.Time", member="timeScale", kind="property")
client.set(type="UnityEngine.Time", member="timeScale", kind="property", value=0.75)
print(client.get(type="UnityEngine.Time", member="timeScale", kind="property"))
client.set(type="UnityEngine.Time", member="timeScale", kind="property", value=before["value"])

# 创建普通 .NET 对象并显式指定重载；对象引用仅在本次运行会话中有效。
text = client.create("System.Text.StringBuilder", ["hello"], parameter_types=["System.String"])
client.invoke(text, "Append", [" world"], parameter_types=["System.String"])
print(client.invoke(text, "ToString", parameter_types=[]))
client.release(text)
```

以上示例使用通用 Unity/.NET 类型，不要求消费工程提供专用调试接口。`create` 是调用普通 .NET 构造函数；Unity 对象应通过现有 Unity API 的静态方法或已定位实例的方法创建、挂接和销毁。

所有方法保留服务返回的 `result`，不会把 `get`、`create`、`invoke` 的 `{value, type}` 自动拆开。`value` 中的对象引用格式为 `{"$ref":"o1"}`。实例成员的 `target` 可以传句柄字符串、引用描述，也可以直接传这些方法的返回对象；静态成员传 `type=`。参数中的引用直接使用 `{"$ref":"o1"}`，不要把整个返回对象放进 `args`。

```python
objects = client.find("UnityEngine.Transform", scope="scene", limit=20)
print(objects)

# 先检查 find 返回的对象描述，选取其中一个句柄后再读取成员。
print(client.inspect("o1"))
print(client.get("o1", "name", kind="property"))
```

句柄示例 `o1` 仅说明调用格式，实际应使用当前响应中的句柄。

## 操作对应关系

| Python 方法 | 请求字段 | 用途 |
| --- | --- | --- |
| `health()` | `GET /health` | 运行实例、帧数、Unity 版本和 Editor 标识 |
| `types(query="", offset=0, limit=100)` | `op=types` | 查询已加载类型 |
| `find(type, scope="scene", offset=0, limit=100)` | `op=find` | 查找场景对象；`loaded` 包含已加载资源 |
| `inspect(target=None, type=None)` | `op=inspect` | 查看成员元数据，不执行属性 getter |
| `get(target=None, member=None, type=None, kind="field", declaring_type=None)` | `op=get` | 读取明确指定的字段或属性 |
| `set(target=None, member=None, value=None, type=None, kind="field", declaring_type=None)` | `op=set` | 写入明确指定的字段或属性 |
| `create(type, args=None, parameter_types=None)` | `op=create` | 调用构造函数 |
| `invoke(target=None, member=None, args=None, type=None, parameter_types=None, declaring_type=None)` | `op=invoke` | 调用实例或静态方法 |
| `release(targets=None, all=False)` | `op=release` | 释放一个、多个或所有桥接引用 |
| `request(op, **kwargs)` | `POST /reflect` | 使用协议字段名发送原始请求 |

`declaring_type` 对应请求中的 `declaringType`，用于明确成员声明类型；`parameter_types` 对应 `parameterTypes`，用于明确方法或构造函数重载。省略 `parameter_types` 时，运行时仅按参数数量选择；有多个同参数数量候选时必须补全类型，不根据 JSON 值猜测重载。实例目标与静态类型只能指定一个。

`release` 仅移除 ClientMCP 持有的引用，不会销毁 Unity 对象，也不会调用 `Dispose`。值类型读取结果是副本；修改副本后需显式写回所属字段或属性。

反射只能访问当前程序实际保留的类型和成员。构建裁剪、AOT 和平台接口支持会影响结果；当前不宣称 IL2CPP 已通过验证。运行时执行的 getter、setter、构造函数和方法具有原代码的副作用，脚本应先查看签名，再按当前实例状态操作。

## 错误与本地契约测试

Python 脚本可统一捕获 `ClientMcpError`，也可以区分 `ClientMcpConnectionError`、`ClientMcpProtocolError` 和 `ClientMcpRemoteError`。远端错误保留 `error_type`、`message`、`http_status`，不会把错误伪装成空数据。

```powershell
python -m unittest discover -s Tools/tests -v
```

这些测试使用本机模拟 HTTP 服务，验证请求、认证错误、异常、JSON 与超时处理；不等同于 Unity 或发布包验证。
