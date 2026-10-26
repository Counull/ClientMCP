using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClientMcp
{
    internal sealed class HttpReply
    {
        public readonly int Status;
        public readonly JObject Body;
        byte[] serializedBody;
        // 活动记录计量与本机发送复用同一份 UTF-8 数据；首次序列化后不再修改 Body。
        public byte[] SerializedBody => serializedBody ?? (serializedBody = Body == null
            ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(Body.ToString(Formatting.None)));
        HttpReply(int status, JObject body) { Status = status; Body = body; }
        public static HttpReply Json(int status, JObject body) => new HttpReply(status, body);
        public static HttpReply Error(int status, string message) => Json(status, new JObject { ["ok"] = false, ["error"] = new JObject { ["type"] = "HttpError", ["message"] = message } });
    }
}
