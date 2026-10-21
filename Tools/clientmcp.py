#!/usr/bin/env python3
"""ClientMCP 的外部脚本入口，仅依赖 Python 标准库。

运行方式见同目录 README.md。模块只传递反射请求，不了解消费工程的业务类型。
"""

import argparse
import json
import math
import socket
import sys
from pathlib import Path
from http.client import HTTPException
from urllib import error, parse, request


# 区分未提供说明与显式传入 None/空字符串；无效说明由运行时统一拒绝。
_DESCRIPTION_UNSET = object()
_OPERATION_DESCRIPTIONS = {
    "types": "查找类型",
    "find": "定位运行对象",
    "inspect": "查看成员元数据",
    "get": "读取成员",
    "set": "写入成员",
    "create": "创建对象",
    "invoke": "调用方法",
    "release": "释放对象引用",
    "plan": "操作缓存反射计划",
    "script": "操作受信任 C# 脚本",
}
_PLAN_ACTION_DESCRIPTIONS = {
    "prepare": "准备缓存反射计划",
    "run": "执行缓存反射计划",
    "result": "查询反射计划执行结果",
    "list": "列出缓存反射计划",
    "release": "释放缓存反射计划",
}


class ClientMcpError(Exception):
    """调用失败的公共基类，便于脚本统一处理错误。"""


class ClientMcpConnectionError(ClientMcpError):
    """连接信息无效、无法连接或请求超时。"""


class ClientMcpProtocolError(ClientMcpError):
    """本地参数无法编码为 JSON，或运行时返回了无效响应。"""


class ClientMcpRemoteError(ClientMcpError):
    """运行时拒绝请求或执行反射时抛出异常。

    error_type 保留运行时错误类型，http_status 保留 HTTP 状态码。
    """

    def __init__(self, error_type, message, http_status=None):
        self.error_type = error_type
        self.message = message
        self.http_status = http_status
        super().__init__(f"{error_type}: {message}")


class _NoRedirect(request.HTTPRedirectHandler):
    """本机接口不需要重定向，避免把 Authorization 转交给其他地址。"""

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class ClientMcp:
    """连接一个明确指定的运行实例，每次调用返回响应中的 result 对象。

    info 是 Unity 输出的连接描述文件；endpoint/token 可以显式覆盖文件值。
    不扫描或自动选择进程，避免脚本误操作另一个正在运行的实例。
    """

    def __init__(self, info=None, *, endpoint=None, token=None, timeout=10.0):
        connection = self._read_info(info) if info is not None else {}
        self.endpoint = endpoint if endpoint is not None else connection.get("endpoint")
        self._token = token if token is not None else connection.get("token")
        self.session_id = connection.get("sessionId")
        self.timeout = timeout

        if not self.endpoint or not self._token:
            raise ClientMcpConnectionError(
                "请通过 info/--info 指定运行实例的连接文件，"
                "或同时提供 endpoint/--endpoint 和 token/--token。"
                "默认连接文件位于系统临时目录的 clientmcp-<进程ID>.json。"
            )
        if not isinstance(self.endpoint, str) or not isinstance(self._token, str):
            raise ClientMcpConnectionError("endpoint 和 token 必须是非空字符串。")
        if any(character in self._token for character in "\r\n"):
            raise ClientMcpConnectionError("token 不能包含换行符。")
        try:
            address = parse.urlsplit(self.endpoint)
            valid = (
                address.scheme == "http"
                and address.hostname in ("127.0.0.1", "localhost", "::1")
                and address.username is None
                and address.password is None
                and address.path in ("", "/")
                and not address.query
                and not address.fragment
            )
            address.port  # 提前检查格式，避免到发送请求时才暴露无效端口。
        except ValueError as exc:
            raise ClientMcpConnectionError("endpoint 不是有效的本机 HTTP 地址。") from exc
        if not valid:
            raise ClientMcpConnectionError(
                "endpoint 必须是 http://127.0.0.1:<端口> 等本机地址，且不包含路径。"
            )
        if not isinstance(timeout, (int, float)) or not math.isfinite(timeout) or timeout <= 0:
            raise ClientMcpConnectionError("timeout 必须是大于 0 的有限秒数。")

        self.endpoint = self.endpoint.rstrip("/")
        # 只直连本机服务，不采用系统代理；连接凭据始终放在请求头中。
        self._opener = request.build_opener(request.ProxyHandler({}), _NoRedirect())

    @staticmethod
    def _read_info(info):
        try:
            with Path(info).open("r", encoding="utf-8-sig") as stream:
                connection = json.load(stream)
        except (OSError, ValueError, TypeError) as exc:
            raise ClientMcpConnectionError(f"无法读取连接文件：{info}；{exc}") from exc
        if not isinstance(connection, dict):
            raise ClientMcpConnectionError("连接文件必须包含 JSON 对象。")
        return connection

    @staticmethod
    def _target_value(target):
        """接受句柄或返回值描述，传输时始终使用句柄字符串。"""
        if isinstance(target, dict):
            if "value" in target:
                return ClientMcp._target_value(target["value"])
            target = target.get("$ref", target.get("id", target.get("handle")))
        if not isinstance(target, str) or not target:
            raise ClientMcpProtocolError("target 必须是非空对象句柄或包含句柄的描述对象。")
        return target

    @staticmethod
    def _receiver(target, type):
        # 实例成员使用 target，静态成员使用 type；显式区分防止静默选错目标。
        if (target is None) == (type is None):
            raise ClientMcpProtocolError("target 和 type 必须且只能提供一个。")
        return {"target": ClientMcp._target_value(target)} if target is not None else {"type": type}

    @staticmethod
    def _decode(body, status):
        try:
            envelope = json.loads(body.decode("utf-8-sig"))
        except (UnicodeError, ValueError) as exc:
            raise ClientMcpProtocolError(f"运行时返回了无效 JSON（HTTP {status}）。") from exc
        if not isinstance(envelope, dict) or not isinstance(envelope.get("ok"), bool):
            raise ClientMcpProtocolError("响应缺少布尔值 ok。")
        if not envelope["ok"]:
            failure = envelope.get("error")
            if not isinstance(failure, dict):
                raise ClientMcpProtocolError("失败响应缺少 error 对象。")
            raise ClientMcpRemoteError(
                str(failure.get("type", "RemoteError")),
                str(failure.get("message", "运行时未提供错误说明。")),
                status,
            )
        if status < 200 or status >= 300:
            raise ClientMcpProtocolError(f"HTTP {status} 与成功响应不一致。")
        if "result" not in envelope:
            raise ClientMcpProtocolError("成功响应缺少 result。")
        return envelope["result"]

    def _send(self, path, payload=None):
        headers = {"Authorization": f"Bearer {self._token}", "Accept": "application/json"}
        data = None
        if payload is not None:
            try:
                data = json.dumps(payload, ensure_ascii=False, allow_nan=False).encode("utf-8")
            except (TypeError, ValueError, UnicodeError) as exc:
                raise ClientMcpProtocolError(f"请求无法编码为 JSON：{exc}") from exc
            headers["Content-Type"] = "application/json; charset=utf-8"
        outgoing = request.Request(self.endpoint + path, data=data, headers=headers)
        try:
            with self._opener.open(outgoing, timeout=self.timeout) as response:
                return self._decode(response.read(), response.status)
        except error.HTTPError as exc:
            # HTTP 错误也可能携带正常的 ClientMCP 错误结构，例如认证失败。
            with exc:
                return self._decode(exc.read(), exc.code)
        except (TimeoutError, socket.timeout) as exc:
            raise ClientMcpConnectionError(f"请求在 {self.timeout:g} 秒内未完成。") from exc
        except (error.URLError, OSError, HTTPException) as exc:
            raise ClientMcpConnectionError(f"无法连接运行实例 {self.endpoint}：{exc}") from exc

    def health(self):
        """读取运行实例、帧数、Unity 版本及 Editor 标识。"""
        return self._send("/health")

    def request(self, op, *, description=_DESCRIPTION_UNSET, **kwargs):
        """发送反射操作；未指定说明时提供通用中文说明，不展开参数和值。"""
        if not isinstance(op, str) or not op:
            raise ClientMcpProtocolError("op 必须是非空字符串。")
        if description is _DESCRIPTION_UNSET:
            description = _OPERATION_DESCRIPTIONS.get(op, "执行反射操作")
            if op == "plan" and isinstance(kwargs.get("action"), str):
                description = _PLAN_ACTION_DESCRIPTIONS.get(kwargs["action"], description)
        return self._send("/reflect", {"op": op, "description": description, **kwargs})

    def types(self, query="", *, offset=0, limit=100, description=_DESCRIPTION_UNSET):
        payload = {"offset": offset, "limit": limit}
        if query:
            payload["query"] = query
        return self.request("types", description=description, **payload)

    def find(self, type, *, scope="scene", offset=0, limit=100, description=_DESCRIPTION_UNSET):
        return self.request("find", type=type, scope=scope, offset=offset, limit=limit,
                            description=description)

    def inspect(self, target=None, *, type=None, description=_DESCRIPTION_UNSET):
        """查看成员元数据；不隐式执行属性 getter。"""
        return self.request("inspect", description=description, **self._receiver(target, type))

    def get(
        self, target=None, member=None, *, type=None, kind="field", declaring_type=None,
        description=_DESCRIPTION_UNSET
    ):
        payload = dict(self._receiver(target, type), member=member, kind=kind)
        if declaring_type is not None:
            payload["declaringType"] = declaring_type
        return self.request("get", description=description, **payload)

    def set(
        self, target=None, member=None, value=None, *, type=None,
        kind="field", declaring_type=None, description=_DESCRIPTION_UNSET
    ):
        payload = dict(self._receiver(target, type), member=member, value=value, kind=kind)
        if declaring_type is not None:
            payload["declaringType"] = declaring_type
        return self.request("set", description=description, **payload)

    def create(self, type, args=None, *, parameter_types=None, description=_DESCRIPTION_UNSET):
        payload = {"type": type, "args": [] if args is None else args}
        if parameter_types is not None:
            payload["parameterTypes"] = parameter_types
        return self.request("create", description=description, **payload)

    def invoke(
        self, target=None, member=None, args=None, *, type=None,
        parameter_types=None, declaring_type=None, description=_DESCRIPTION_UNSET
    ):
        payload = dict(self._receiver(target, type), member=member, args=[] if args is None else args)
        if parameter_types is not None:
            payload["parameterTypes"] = parameter_types
        if declaring_type is not None:
            payload["declaringType"] = declaring_type
        return self.request("invoke", description=description, **payload)

    def release(self, targets=None, *, all=False, description=_DESCRIPTION_UNSET):
        """释放桥接层持有的对象引用；不会销毁对象或执行 Dispose。"""
        if all:
            if targets is not None:
                raise ClientMcpProtocolError("释放全部引用时不要同时传 targets。")
            return self.request("release", all=True, description=description)
        if targets is None:
            raise ClientMcpProtocolError("请指定 targets，或明确传入 all=True。")
        if isinstance(targets, (str, dict)):
            targets = [targets]
        return self.request("release", targets=[self._target_value(target) for target in targets],
                            description=description)

    def prepare_plan(self, plan_id, steps, *, description=_DESCRIPTION_UNSET):
        """提交计划定义；保留参数与步骤引用，由运行时验证并缓存。"""
        return self.request("plan", action="prepare", planId=plan_id, steps=steps,
                            description=description)

    def run_plan(
        self, plan_id, operation_id, *, session_id, parameters=None,
        description=_DESCRIPTION_UNSET
    ):
        """执行调用方明确编号的操作；不自动重试，失败回执也原样返回。"""
        payload = {"planId": plan_id, "operationId": operation_id, "sessionId": session_id}
        if parameters is not None:
            payload["parameters"] = parameters
        return self.request("plan", action="run", description=description, **payload)

    def plan_result(self, operation_id, *, session_id, description=_DESCRIPTION_UNSET):
        """查询原操作回执；会话失效时不自动换编号或重新执行。"""
        return self.request("plan", action="result", operationId=operation_id,
                            sessionId=session_id, description=description)

    def list_plans(self, *, description=_DESCRIPTION_UNSET):
        return self.request("plan", action="list", description=description)

    def release_plan(self, plan_id, *, session_id, description=_DESCRIPTION_UNSET):
        """释放指定会话的计划；不在客户端维护计划或运行对象缓存。"""
        return self.request("plan", action="release", planId=plan_id,
                            sessionId=session_id, description=description)

    def prepare_script(self, script_id, code, *, description=_DESCRIPTION_UNSET):
        return self.request("script", action="prepare", scriptId=script_id,
                            code=code, description=description)

    def run_script(self, script_id, operation_id, *, session_id, parameters=None,
                   description=_DESCRIPTION_UNSET):
        return self.request("script", action="run", scriptId=script_id,
                            operationId=operation_id, sessionId=session_id,
                            parameters={} if parameters is None else parameters,
                            description=description)

    def script_result(self, operation_id, *, session_id, description=_DESCRIPTION_UNSET):
        return self.request("script", action="result", operationId=operation_id,
                            sessionId=session_id, description=description)

    def list_scripts(self, *, description=_DESCRIPTION_UNSET):
        return self.request("script", action="list", description=description)

    def release_script(self, script_id, *, session_id, description=_DESCRIPTION_UNSET):
        return self.request("script", action="release", scriptId=script_id,
                            sessionId=session_id, description=description)


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="向明确指定的本机 Unity 运行实例发送通用反射请求。",
        epilog="操作：types、find、inspect、get、set、create、invoke、release、plan。JSON 参数 '-' 表示从标准输入读取。",
    )
    parser.add_argument("payload", nargs="?", help="JSON 请求对象，或 '-' 从标准输入读取")
    parser.add_argument("--info", help="运行实例写出的连接文件路径")
    parser.add_argument("--endpoint", help="显式本机 HTTP 地址，例如 http://127.0.0.1:49152")
    parser.add_argument("--token", help="显式 Bearer token；日常推荐使用 --info")
    parser.add_argument("--timeout", type=float, default=10.0, help="请求超时秒数，默认 10")
    parser.add_argument("--health", action="store_true", help="读取运行实例健康信息")
    options = parser.parse_args(argv)
    if options.payload is None and not options.health:
        parser.print_help()
        return 0
    if options.health and options.payload is not None:
        parser.error("--health 不能与 JSON 请求同时使用。")
    try:
        client = ClientMcp(
            options.info, endpoint=options.endpoint, token=options.token, timeout=options.timeout
        )
        if options.health:
            result = client.health()
        else:
            source = sys.stdin.read() if options.payload == "-" else options.payload
            try:
                payload = json.loads(source)
            except ValueError as exc:
                raise ClientMcpProtocolError(f"请求不是有效 JSON：{exc}") from exc
            if not isinstance(payload, dict) or "op" not in payload:
                raise ClientMcpProtocolError("JSON 请求必须是包含 op 的对象。")
            # CLI 原始 JSON 由调用者负责说明，避免缺少说明时被 helper 默认值掩盖。
            if "description" not in payload:
                raise ClientMcpProtocolError("JSON 请求必须包含 description，说明当前操作。")
            result = client.request(**payload)
        print(json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False))
        return 0
    except ClientMcpError as exc:
        print(f"ClientMCP：{exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    # 外部脚本常被终端或 AI 进程重定向，固定 UTF-8 以保留中文和其他 Unicode 值。
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
    sys.exit(main())
