using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ClientMcp.Server;

/// <summary>仅配置传输与接入授权；令牌来自环境，不写入仓库或日志。</summary>
public sealed class RelayOptions
{
    public string Url { get; init; } = "http://127.0.0.1:55707";
    public string DeveloperToken { get; init; }
    public string ClientToken { get; init; }
    public int RequestTimeoutMilliseconds { get; init; } = 15000;
    public int HelloTimeoutMilliseconds { get; init; } = 10000;
    public int HeartbeatTimeoutMilliseconds { get; init; } = 45000;
    public const int MaxMessageBytes = 1024 * 1024;
    public const int MaxConnections = 64;
    public const int MaxPendingRequests = 256;
    public const int MaxPendingPerClient = 64;

    public static RelayOptions FromEnvironment() => new()
    {
        Url = Environment.GetEnvironmentVariable("CLIENTMCP_URL") ?? "http://127.0.0.1:55707",
        DeveloperToken = Environment.GetEnvironmentVariable("CLIENTMCP_DEVELOPER_TOKEN"),
        ClientToken = Environment.GetEnvironmentVariable("CLIENTMCP_CLIENT_TOKEN")
    };

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DeveloperToken) || DeveloperToken.Length < 32 ||
            string.IsNullOrWhiteSpace(ClientToken) || ClientToken.Length < 32 || DeveloperToken == ClientToken ||
            DeveloperToken.Any(char.IsControl) || ClientToken.Any(char.IsControl))
            throw new ArgumentException("Set distinct CLIENTMCP_DEVELOPER_TOKEN and CLIENTMCP_CLIENT_TOKEN values of at least 32 characters.");
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new ArgumentException("CLIENTMCP_URL must be one HTTP(S) origin.");
        if (uri.Scheme == "http" && !IsLoopback(uri.Host))
            throw new ArgumentException("Non-loopback listening requires HTTPS and a configured ASP.NET Core certificate.");
        if (RequestTimeoutMilliseconds < 100 || HelloTimeoutMilliseconds < 100 || HeartbeatTimeoutMilliseconds < 100)
            throw new ArgumentException("Timeouts must be at least 100 milliseconds.");
    }

    internal static bool IsLoopback(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

    internal static bool TokenMatches(string supplied, string expected) => supplied != null &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}
