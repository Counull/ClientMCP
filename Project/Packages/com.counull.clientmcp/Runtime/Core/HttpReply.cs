using Newtonsoft.Json.Linq;

namespace ClientMcp
{
    internal sealed class HttpReply
    {
        public readonly int Status;
        public readonly JObject Body;
        HttpReply(int status, JObject body) { Status = status; Body = body; }
        public static HttpReply Json(int status, JObject body) => new HttpReply(status, body);
        public static HttpReply Error(int status, string message) => Json(status, new JObject { ["ok"] = false, ["error"] = new JObject { ["type"] = "HttpError", ["message"] = message } });
    }
}
