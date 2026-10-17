"""外部客户端的 HTTP 契约测试；不依赖 Unity 或第三方库。"""

import contextlib
import io
import json
import sys
import tempfile
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from clientmcp import (  # noqa: E402
    ClientMcp,
    ClientMcpConnectionError,
    ClientMcpProtocolError,
    ClientMcpRemoteError,
    main,
)


class TestClientMcp(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        class Handler(BaseHTTPRequestHandler):
            def log_message(self, format, *args):
                pass

            def send_json(self, status, payload):
                data = json.dumps(payload).encode("utf-8")
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                try:
                    self.wfile.write(data)
                except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                    pass  # 超时测试会主动关闭连接，这是被测试的预期行为。

            def authorized(self):
                if self.headers.get("Authorization") == "Bearer test-private-token":
                    return True
                self.send_json(401, {"ok": False, "error": {"type": "Unauthorized", "message": "凭据无效"}})
                return False

            def do_GET(self):
                if self.authorized():
                    self.send_json(200, {"ok": True, "result": {
                        "sessionId": "session-test", "frameCount": 42,
                        "unityVersion": "test", "isEditor": False,
                    }})

            def do_POST(self):
                if not self.authorized():
                    return
                payload = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                if payload["op"] == "remote-error":
                    self.send_json(400, {"ok": False, "error": {
                        "type": "System.InvalidOperationException", "message": "明确的执行错误",
                    }})
                elif payload["op"] == "invalid-json":
                    self.send_response(200)
                    self.end_headers()
                    self.wfile.write(b"this is not json")
                elif payload["op"] == "missing-result":
                    self.send_json(200, {"ok": True})
                elif payload["op"] == "slow":
                    time.sleep(0.15)
                    self.send_json(200, {"ok": True, "result": {}})
                else:
                    self.send_json(200, {"ok": True, "result": {"received": payload, "path": self.path}})

        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        cls.server.daemon_threads = True
        cls.server_thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.server_thread.start()
        cls.endpoint = f"http://127.0.0.1:{cls.server.server_port}"

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.server_thread.join(timeout=2)

    def client(self, **kwargs):
        return ClientMcp(endpoint=self.endpoint, token="test-private-token", **kwargs)

    def test_info_file_health_and_explicit_overrides(self):
        with tempfile.TemporaryDirectory() as directory:
            info = Path(directory) / "connection.json"
            info.write_text(json.dumps({
                "endpoint": self.endpoint, "token": "test-private-token", "sessionId": "session-test",
            }), encoding="utf-8")
            client = ClientMcp(info)
            self.assertEqual(client.session_id, "session-test")
            self.assertEqual(client.health()["frameCount"], 42)
            self.assertFalse(client.health()["isEditor"])
            self.assertEqual(ClientMcp(info, token="test-private-token").health()["sessionId"], "session-test")

    def test_request_and_unicode_value(self):
        result = self.client().request("set", target="o1", member="name", value="运行对象")
        self.assertEqual(result["path"], "/reflect")
        self.assertEqual(result["received"]["value"], "运行对象")
        self.assertEqual(result["received"]["description"], "写入成员")

    def test_helpers_send_default_descriptions_in_http_body(self):
        client = self.client()
        operations = (
            ("types", lambda: client.types(), "查找类型"),
            ("find", lambda: client.find("Example.Type"), "定位运行对象"),
            ("inspect", lambda: client.inspect("o1"), "查看成员元数据"),
            ("get", lambda: client.get("o1", "Value"), "读取成员"),
            ("set", lambda: client.set("o1", "Value", 23), "写入成员"),
            ("create", lambda: client.create("Example.Type"), "创建对象"),
            ("invoke", lambda: client.invoke("o1", "UpdateValue"), "调用方法"),
            ("release", lambda: client.release("o1"), "释放对象引用"),
        )
        for operation, call, description in operations:
            with self.subTest(operation=operation):
                received = call()["received"]
                self.assertEqual(received["op"], operation)
                self.assertEqual(received["description"], description)

    def test_explicit_descriptions_preserve_helper_payloads(self):
        client = self.client()
        operations = (
            ("types", ("Example",), {"offset": 2, "limit": 3}),
            ("find", ("Example.Type",), {"scope": "loaded", "offset": 2, "limit": 3}),
            ("inspect", ({"$ref": "o1"},), {}),
            ("get", ("o1", "Value"), {"kind": "property", "declaring_type": "Example.Base"}),
            ("set", ("o1", "Value", {"x": 17, "text": "值保持不变"}), {}),
            ("create", ("Example.Type", ["原始参数"]), {"parameter_types": ["System.String"]}),
            ("invoke", ("o1", "Accept", [{"$ref": "o2"}]),
             {"parameter_types": ["System.Object"], "declaring_type": "Example.Base"}),
            ("release", (["o1", {"$ref": "o2"}],), {}),
            ("release", (), {"all": True}),
        )
        for operation, args, kwargs in operations:
            with self.subTest(operation=operation, kwargs=kwargs):
                helper = getattr(client, operation)
                baseline = helper(*args, **kwargs)["received"]
                description = "  说明这次操作的用途  "
                received = helper(*args, **kwargs, description=description)["received"]
                self.assertEqual(received.pop("description"), description)
                baseline.pop("description")
                self.assertEqual(received, baseline)

    def test_explicit_invalid_descriptions_are_not_replaced(self):
        client = self.client()
        # 客户端保留显式输入，让实际运行时统一校验；mock 只回显收到的 HTTP 正文。
        for description in (None, "", " \t ", "说明\n换行", "长" * 201):
            with self.subTest(description=description):
                direct = client.request("get", target="o1", member="Value",
                                        description=description)["received"]
                helper = client.get("o1", "Value", description=description)["received"]
                self.assertEqual(direct["description"], description)
                self.assertEqual(helper["description"], description)

    def test_unknown_operation_uses_generic_description(self):
        received = self.client().request("unknown-operation")["received"]
        self.assertEqual(received["description"], "执行反射操作")

    def test_empty_type_query_is_omitted(self):
        self.assertNotIn("query", self.client().types()["received"])

    def test_authentication_error_is_remote_error(self):
        with self.assertRaises(ClientMcpRemoteError) as caught:
            ClientMcp(endpoint=self.endpoint, token="invalid").health()
        self.assertEqual(caught.exception.error_type, "Unauthorized")
        self.assertEqual(caught.exception.http_status, 401)

    def test_remote_execution_error_is_preserved(self):
        with self.assertRaises(ClientMcpRemoteError) as caught:
            self.client().request("remote-error")
        self.assertEqual(caught.exception.error_type, "System.InvalidOperationException")
        self.assertEqual(caught.exception.message, "明确的执行错误")

    def test_malformed_response_is_protocol_error(self):
        for operation in ("invalid-json", "missing-result"):
            with self.subTest(operation=operation), self.assertRaises(ClientMcpProtocolError):
                self.client().request(operation)

    def test_request_timeout_is_connection_error(self):
        with self.assertRaises(ClientMcpConnectionError) as caught:
            self.client(timeout=0.03).request("slow")
        self.assertIn("0.03", str(caught.exception))

    def test_descriptor_target_and_reference_argument(self):
        result = self.client().invoke(
            {"value": {"$ref": "o1"}, "type": "System.Object"},
            "Accept", [{"$ref": "o2"}], parameter_types=["System.Object"], declaring_type="Example.Type",
        )["received"]
        self.assertEqual(result["target"], "o1")
        self.assertEqual(result["args"], [{"$ref": "o2"}])
        self.assertEqual(result["parameterTypes"], ["System.Object"])
        self.assertEqual(result["declaringType"], "Example.Type")

    def test_static_receiver_and_release(self):
        client = self.client()
        static = client.get(type="UnityEngine.Time", member="timeScale", kind="property")["received"]
        self.assertNotIn("target", static)
        self.assertEqual(static["type"], "UnityEngine.Time")
        self.assertEqual(client.release(["o1", {"$ref": "o2"}])["received"]["targets"], ["o1", "o2"])
        self.assertTrue(client.release(all=True)["received"]["all"])

    def test_invalid_local_parameters_fail_before_connecting(self):
        client = self.client()
        with self.assertRaises(ClientMcpProtocolError):
            client.get("o1", "x", type="Example.Type")
        with self.assertRaises(ClientMcpProtocolError):
            client.request("set", value=float("nan"))
        with self.assertRaises(ClientMcpProtocolError):
            client.release()
        with self.assertRaises(ClientMcpConnectionError):
            ClientMcp()
        with self.assertRaises(ClientMcpConnectionError):
            ClientMcp(endpoint="http://example.com", token="private")

    def test_cli_success_and_error_exit_codes(self):
        options = ["--endpoint", self.endpoint, "--token", "test-private-token"]
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            self.assertEqual(main(options + [
                '{"op":"types","query":"System.String","description":"查询字符串类型"}'
            ]), 0)
        received = json.loads(output.getvalue())["received"]
        self.assertEqual(received["query"], "System.String")
        self.assertEqual(received["description"], "查询字符串类型")
        errors = io.StringIO()
        with contextlib.redirect_stderr(errors):
            self.assertEqual(main(options + ["{bad json"]), 1)
        self.assertIn("JSON", errors.getvalue())
        self.assertNotIn("test-private-token", errors.getvalue())

    def test_cli_requires_explicit_description(self):
        options = ["--endpoint", self.endpoint, "--token", "test-private-token"]
        errors = io.StringIO()
        with contextlib.redirect_stderr(errors):
            self.assertEqual(main(options + ['{"op":"types"}']), 1)
        self.assertIn("description", errors.getvalue())
        self.assertNotIn("test-private-token", errors.getvalue())

    def test_cli_preserves_explicit_invalid_description_for_runtime_validation(self):
        options = ["--endpoint", self.endpoint, "--token", "test-private-token"]
        for description in (None, ""):
            with self.subTest(description=description):
                output = io.StringIO()
                payload = json.dumps({"op": "types", "description": description})
                with contextlib.redirect_stdout(output):
                    self.assertEqual(main(options + [payload]), 0)
                self.assertEqual(json.loads(output.getvalue())["received"]["description"], description)


if __name__ == "__main__":
    unittest.main()
