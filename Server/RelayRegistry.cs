using Newtonsoft.Json.Linq;

namespace ClientMcp.Server;

/// <summary>只保存在线连接和待回复请求；连接身份阻止断线后的迟到应答命中新连接。</summary>
internal sealed class RelayRegistry(RelayOptions options)
{
    readonly object gate = new();
    readonly Dictionary<string, RelayClient> clients = new(StringComparer.Ordinal);
    readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
    int connectionCount;

    sealed class Pending(RelayClient client)
    {
        public readonly RelayClient Client = client;
        public readonly TaskCompletionSource<JObject> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public bool ReserveConnection()
    {
        lock (gate)
        {
            if (connectionCount >= RelayOptions.MaxConnections) return false;
            connectionCount++;
            return true;
        }
    }

    public void ReleaseConnection() { lock (gate) connectionCount--; }

    public bool Register(RelayClient client)
    {
        lock (gate) return clients.TryAdd(client.InstanceId, client);
    }

    public void MarkReady(RelayClient client)
    {
        lock (gate)
        {
            if (clients.TryGetValue(client.InstanceId, out var registered) && ReferenceEquals(registered, client)) client.Ready = true;
        }
    }

    public JObject ListInstances()
    {
        lock (gate) return new JObject
        {
            ["count"] = clients.Values.Count(client => client.Ready),
            ["instances"] = new JArray(clients.Values.Where(client => client.Ready).OrderBy(client => client.InstanceId, StringComparer.Ordinal).Select(client => client.Describe()))
        };
    }

    public void Disconnect(RelayClient client)
    {
        lock (gate)
        {
            if (client.InstanceId != null && clients.TryGetValue(client.InstanceId, out var registered) && ReferenceEquals(registered, client))
                clients.Remove(client.InstanceId);
            foreach (var pair in pending.Where(pair => ReferenceEquals(pair.Value.Client, client)).ToArray())
            {
                pending.Remove(pair.Key);
                client.PendingCount--;
                pair.Value.Completion.TrySetResult(Failure("RelayDisconnected", "The runtime disconnected; the operation may already have executed. Query the original operationId after reconnecting before retrying a mutation."));
            }
        }
    }

    public void Complete(RelayClient client, string requestId, JObject body)
    {
        lock (gate)
        {
            if (!pending.TryGetValue(requestId, out var request) || !ReferenceEquals(request.Client, client)) return;
            pending.Remove(requestId);
            client.PendingCount--;
            request.Completion.TrySetResult(body);
        }
    }

    public async Task<JObject> ForwardAsync(string instanceId, JObject command, CancellationToken cancellation)
    {
        var requestId = Guid.NewGuid().ToString("N");
        Pending request;
        lock (gate)
        {
            if (!clients.TryGetValue(instanceId, out var client) || !client.Ready) return Failure("InstanceOffline", "The requested instance is not online.");
            if (pending.Count >= RelayOptions.MaxPendingRequests || client.PendingCount >= RelayOptions.MaxPendingPerClient)
                return Failure("RelayBusy", "The pending request limit was reached; no request was sent.");
            request = new Pending(client);
            pending.Add(requestId, request);
            client.PendingCount++;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(options.RequestTimeoutMilliseconds);
        try
        {
            await request.Client.SendAsync(new JObject { ["kind"] = "request", ["requestId"] = requestId, ["command"] = command }, deadline.Token);
            return await request.Completion.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            return Failure("RelayTimeout", "The relay stopped waiting; the operation may already have executed. Query the original operationId or retry with that same operationId. The relay never replays a request.");
        }
        catch (InvalidDataException)
        {
            return Failure("RelayMessageTooLarge", "The request including relay envelope exceeds 1 MiB; no request was sent.");
        }
        catch (Exception error) when (error is IOException || error is System.Net.WebSockets.WebSocketException || error is ObjectDisposedException)
        {
            return Failure("RelayDisconnected", "The transport disconnected; the operation may already have executed. Query the original operationId after reconnecting.");
        }
        finally
        {
            lock (gate)
            {
                if (pending.Remove(requestId)) request.Client.PendingCount--;
            }
        }
    }

    public static JObject Failure(string type, string message) => new()
    {
        ["ok"] = false, ["error"] = new JObject { ["type"] = type, ["message"] = message }
    };
}
