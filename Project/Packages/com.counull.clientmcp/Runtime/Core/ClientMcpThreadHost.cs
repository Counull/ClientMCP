using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClientMcp
{
    /// <summary>.NET 专用诊断线程 Adapter；业务有线程约束时须通过其已有队列入口封送。</summary>
    public sealed class ClientMcpThreadHost : IDisposable
    {
        readonly ManualResetEventSlim stop = new ManualResetEventSlim();
        readonly TaskCompletionSource<bool> ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly Thread thread;
        readonly Action<string, bool> log;
        public ClientMcpThreadHost(Action<RegisteredObjectHost> register, ClientMcpOptions options, Action<string, bool> log = null)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (options == null) throw new ArgumentNullException(nameof(options));
            this.log = log ?? ((message, warning) => { });
            thread = new Thread(() => Run(register, options)) { IsBackground = true, Name = "ClientMCP .NET host" };
            thread.Start();
        }
        public Task Ready => ready.Task;
        void Run(Action<RegisteredObjectHost> register, ClientMcpOptions options)
        {
            try
            {
                var objects = new RegisteredObjectHost();
                register(objects);
                using (var host = new ClientMcpHost(objects, log: log))
                {
                    if (stop.IsSet) { ready.TrySetCanceled(); return; }
                    host.Start(options);
                    ready.TrySetResult(true);
                    while (!stop.IsSet) { host.Pump(); stop.Wait(5); }
                }
            }
            catch (Exception error)
            {
                ready.TrySetException(error);
                log("ClientMCP 诊断线程已停止：" + error.GetType().Name, true);
            }
        }
        public void Dispose()
        {
            stop.Set();
            // 不强杀已开始的脚本，也不在错误线程释放其会话。
            if (Thread.CurrentThread != thread && !thread.Join(1000))
                log("ClientMCP 等待已开始的操作结束；脚本无法强制中止。", true);
        }
    }
}
