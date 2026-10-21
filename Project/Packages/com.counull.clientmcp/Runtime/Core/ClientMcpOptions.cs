namespace ClientMcp
{
    /// <summary>显式接入选项。默认仅开本机入口；中转必须提供地址和独立的客户端令牌。</summary>
    public sealed class ClientMcpOptions
    {
        public int Port;
        public IScriptCompiler ScriptCompiler;
        public string InfoPath;
        public int RequestTimeoutMilliseconds = 10000;
        public string RelayUrl;
        public string RelayToken;
        public string InstanceName;
    }

}
