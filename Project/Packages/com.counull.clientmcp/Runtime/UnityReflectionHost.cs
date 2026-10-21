using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace ClientMcp
{
    public sealed class UnityReflectionHost : IReflectionHost
    {
        public IEnumerable<HostObject> FindObjects(Type type, string scope)
        {
            if (!typeof(UnityObject).IsAssignableFrom(type))
                throw new ArgumentException("find 只查找 UnityEngine.Object；普通 C# 对象请从字段、返回值或 create 获取句柄。");
            scope = scope ?? "scene";
            if (scope != "scene" && scope != "loaded") throw new ArgumentException("scope 只能是 scene 或 loaded。");
            return Resources.FindObjectsOfTypeAll(type)
                .Where(value => value != null && (scope == "loaded" || IsSceneObject(value)))
                .OrderBy(Id, StringComparer.Ordinal).Select(value => new HostObject(value, value.name, Id(value))).ToArray();
        }
        public bool RequiresExistingObject(Type type) => typeof(UnityObject).IsAssignableFrom(type);
        public void EnsureAlive(object value)
        {
            if (value is UnityObject unityObject && unityObject == null)
                throw new InvalidOperationException("句柄引用的 Unity 对象已销毁，句柄已过期。");
        }
        static bool IsSceneObject(UnityObject value)
        {
            var gameObject = value as GameObject;
            if (gameObject == null && value is Component component) gameObject = component.gameObject;
            return gameObject != null && gameObject.scene.IsValid() && gameObject.scene.isLoaded;
        }
        static string Id(UnityObject value)
        {
#if UNITY_6000_5_OR_NEWER
            return value.GetEntityId().ToString();
#else
            return value.GetInstanceID().ToString(CultureInfo.InvariantCulture);
#endif
        }
    }
}
