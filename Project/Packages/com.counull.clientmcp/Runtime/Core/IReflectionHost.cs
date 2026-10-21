using System;
using System.Collections.Generic;
using System.Linq;

namespace ClientMcp
{
    public sealed class HostObject
    {
        public readonly object Value;
        public readonly string Name;
        public readonly string Id;
        public HostObject(object value, string name, string id)
        { Value = value ?? throw new ArgumentNullException(nameof(value)); Name = name; Id = id; }
    }

    // 仅对象发现和生命周期因宿主而异；普通反射与句柄留在核心。
    public interface IReflectionHost
    {
        IEnumerable<HostObject> FindObjects(Type type, string scope);
        bool RequiresExistingObject(Type type);
        void EnsureAlive(object value);
    }

    /// <summary>.NET 根对象显式注册；所有访问与注册均由同一宿主执行上下文串行调用。</summary>
    public sealed class RegisteredObjectHost : IReflectionHost
    {
        readonly Dictionary<string, object> roots = new Dictionary<string, object>(StringComparer.Ordinal);
        readonly Func<object, bool> isAlive;
        public RegisteredObjectHost(Func<object, bool> isAlive = null) { this.isAlive = isAlive; }
        public void Register(string name, object value)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("根对象名称不能为空。");
            if (roots.ContainsKey(name)) throw new InvalidOperationException("根对象名称已注册。");
            roots.Add(name, value ?? throw new ArgumentNullException(nameof(value)));
        }
        public bool Unregister(string name) => roots.Remove(name);
        public object GetRoot(string name)
        {
            if (!roots.TryGetValue(name, out var value)) throw new KeyNotFoundException("根对象未注册：" + name);
            EnsureAlive(value);
            return value;
        }
        public IEnumerable<HostObject> FindObjects(Type type, string scope)
        {
            if (scope != null && scope != "roots") throw new ArgumentException(".NET scope 只能是 roots。");
            return roots.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Where(pair => type.IsInstanceOfType(pair.Value) && (isAlive == null || isAlive(pair.Value)))
                .Select(pair => new HostObject(pair.Value, pair.Key, pair.Key)).ToArray();
        }
        public bool RequiresExistingObject(Type type) => false;
        public void EnsureAlive(object value)
        {
            if (isAlive != null && !isAlive(value)) throw new InvalidOperationException("宿主对象已失效，句柄已过期。");
        }
    }
}
