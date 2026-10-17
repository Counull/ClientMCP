using System;
using UnityEngine;

namespace ClientMcp.Development
{
    /// <summary>仅供开发工程验收，不随 UPM 包分发，也不会在未启用时创建对象。</summary>
    public sealed class RuntimeProbe : MonoBehaviour
    {
        public int Frames;
        public ProbeValue Value = new ProbeValue(3);
        private int _counter;

        public int Counter => _counter;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateWhenRequested()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--clientmcp-probe") < 0)
                return;
            var owner = new GameObject("ClientMCP Runtime Probe");
            DontDestroyOnLoad(owner);
            owner.AddComponent<RuntimeProbe>();
        }

        private void Update()
        {
            // 外部脚本可在两次读取之间观察变化，确认主循环持续执行。
            Frames++;
            _counter++;
        }

        public class ProbeBaseValue
        {
            private int _baseValue = 7;

            public int ReadBaseValue() => _baseValue;
        }

        /// <summary>普通 C# 对象用于检验构造、私有继承字段、引用参数和重载。</summary>
        public sealed class ProbeValue : ProbeBaseValue
        {
            private int _value;
            public int GetterReads;
            public Vector3 Position = new Vector3(1, 2, 3);
            public static int Shared { get; set; } = 5;

            public ProbeValue() : this(0) { }

            public ProbeValue(int value)
            {
                _value = value;
            }

            public int SideEffectValue
            {
                get
                {
                    GetterReads++;
                    return _value;
                }
            }

            public int Accept(ProbeValue other) => other._value;
            public string Echo(int value) => "int:" + value;
            public string Echo(string value) => "string:" + value;

            public void Fail()
            {
                throw new InvalidOperationException("probe-root-cause");
            }
        }
    }
}
