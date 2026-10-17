using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace ClientMcp
{
    /// <summary>
    /// 一个连接持有一个反射会话：只负责类型、对象句柄和反射元操作，不解释业务含义。
    /// 所有入口都必须由传输层调度到 Unity 主线程；这里不创建线程，也不自动执行属性 getter。
    /// </summary>
    public sealed class ReflectionSession
    {
        private const int MaxHandles = 4096;
        private const BindingFlags DeclaredMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                     BindingFlags.Instance | BindingFlags.Static |
                                                     BindingFlags.DeclaredOnly;

        private readonly Dictionary<string, object> _objects = new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly Dictionary<object, string> _handles = new Dictionary<object, string>(ReferenceIdentityComparer.Instance);
        private readonly ReflectionValueConverter _converter;
        private ulong _nextHandle = 1;

        public ReflectionSession()
        {
            _converter = new ReflectionValueConverter(ResolveHandle);
        }

        /// <summary>执行单次元操作；异常保留实际类型和根因，由传输层统一转成错误响应。</summary>
        public JObject Execute(JObject request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            // 参数转换也可能显式执行构造函数或 setter，因此在整个请求边界统一剥离反射异常包装。
            return (JObject)Call(() => ExecuteOperation(request));
        }

        private JObject ExecuteOperation(JObject request)
        {
            switch (RequiredString(request, "op"))
            {
                case "types": return ListTypes(request);
                case "find": return FindObjects(request);
                case "inspect": return Inspect(request);
                case "get": return Get(request);
                case "set": return Set(request);
                case "create": return Create(request);
                case "invoke": return Invoke(request);
                case "release": return Release(request);
                default: throw new ArgumentException("未知 op。可用值：types、find、inspect、get、set、create、invoke、release。");
            }
        }

        /// <summary>断开连接或停止服务时释放强引用。句柄编号不复用，避免旧脚本误操作新对象。</summary>
        public void Clear()
        {
            _objects.Clear();
            _handles.Clear();
        }

        private JObject ListTypes(JObject request)
        {
            var page = ReadPage(request);
            var query = OptionalString(request, "query") ?? string.Empty;
            var types = LoadedTypes()
                .Where(type => TypeName(type).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(TypeName, StringComparer.Ordinal)
                .ThenBy(type => type.Assembly.FullName, StringComparer.Ordinal)
                .ToArray();

            var items = new JArray();
            foreach (var type in types.Skip(page.Offset).Take(page.Limit))
            {
                items.Add(new JObject
                {
                    ["type"] = TypeName(type),
                    ["assembly"] = type.Assembly.GetName().Name,
                    ["assemblyQualifiedName"] = type.AssemblyQualifiedName,
                    ["isValueType"] = type.IsValueType,
                    ["isAbstract"] = type.IsAbstract,
                    ["isInterface"] = type.IsInterface,
                    ["containsGenericParameters"] = type.ContainsGenericParameters
                });
            }
            return Paged(items, types.Length, page);
        }

        private JObject FindObjects(JObject request)
        {
            var type = ResolveType(RequiredString(request, "type"));
            if (!typeof(UnityObject).IsAssignableFrom(type))
                throw new ArgumentException("find 只查找 UnityEngine.Object；普通 C# 对象请从字段、返回值或 create 获取句柄。");

            var scope = OptionalString(request, "scope") ?? "scene";
            if (scope != "scene" && scope != "loaded")
                throw new ArgumentException("scope 只能是 scene 或 loaded。");

            var page = ReadPage(request);
            // FindObjectsOfTypeAll 包含未激活对象。scene 排除项目资产和未加载场景；loaded 明确包含资产。
            var objects = Resources.FindObjectsOfTypeAll(type)
                .Where(item => item != null && (scope == "loaded" || IsSceneObject(item)))
                .OrderBy(UnityObjectId, StringComparer.Ordinal)
                .ToArray();
            var items = new JArray();
            foreach (var item in objects.Skip(page.Offset).Take(page.Limit))
            {
                var description = Reference(item);
                description["name"] = item.name;
                description["instanceId"] = UnityObjectId(item);
                items.Add(description);
            }
            return Paged(items, objects.Length, page);
        }

        private JObject Inspect(JObject request)
        {
            var target = ReadTarget(request);
            var page = ReadPage(request);
            var members = new List<JObject>();
            var signatures = new HashSet<string>(StringComparer.Ordinal);

            // 仅列元数据。属性值和对象 ToString 都可能执行用户代码，因此绝不在 inspect 中读取。
            foreach (var type in Hierarchy(target.Type, ReadDeclaringType(request, target.Type)))
            {
                foreach (var field in type.GetFields(DeclaredMembers).Where(item => item.IsStatic == target.IsStatic))
                    members.Add(DescribeField(field));
                foreach (var property in type.GetProperties(DeclaredMembers).Where(item => IsStatic(item) == target.IsStatic))
                    members.Add(DescribeProperty(property));
                foreach (var method in type.GetMethods(DeclaredMembers).Where(item => item.IsStatic == target.IsStatic))
                {
                    if (IsAccessor(method) || !signatures.Add(MethodKey(method)))
                        continue;
                    members.Add(DescribeMethod(method));
                }
            }

            if (target.IsStatic)
            {
                foreach (var constructor in target.Type.GetConstructors(DeclaredMembers).Where(item => !item.IsStatic))
                    members.Add(DescribeMethod(constructor));
            }

            var sorted = members.OrderBy(item => (string)item["kind"], StringComparer.Ordinal)
                .ThenBy(item => (string)item["name"], StringComparer.Ordinal)
                .ThenBy(item => (string)item["signature"], StringComparer.Ordinal)
                .ThenBy(item => (string)item["declaringType"], StringComparer.Ordinal)
                .ToArray();
            var result = Paged(new JArray(sorted.Skip(page.Offset).Take(page.Limit)), sorted.Length, page);
            result["type"] = TypeName(target.Type);
            result["isStatic"] = target.IsStatic;
            result["isValueType"] = target.Type.IsValueType;
            if (target.Type.IsValueType && !target.IsStatic)
                result["copySemantics"] = "此句柄持有装箱后的值副本；修改后须用 set 将该句柄写回原父成员。";
            return result;
        }

        private JObject Get(JObject request)
        {
            var target = ReadTarget(request);
            var member = FindValueMember(request, target);
            if (member is FieldInfo field)
                return Value(field.GetValue(target.Instance), field.FieldType);

            var property = (PropertyInfo)member;
            ValidateProperty(property);
            var getter = property.GetGetMethod(true);
            if (getter == null)
                throw new InvalidOperationException("属性没有 getter：" + property.Name);
            return Value(getter.Invoke(target.Instance, null), property.PropertyType);
        }

        private JObject Set(JObject request)
        {
            if (!request.TryGetValue("value", out var token))
                throw new ArgumentException("set 必须提供 value；写入空引用请显式传 null。");
            var target = ReadTarget(request);
            var member = FindValueMember(request, target);
            Type valueType;
            object value;
            if (member is FieldInfo field)
            {
                if (field.IsInitOnly || field.IsLiteral)
                    throw new InvalidOperationException("只读或常量字段不可写入：" + field.Name);
                valueType = field.FieldType;
                value = _converter.Convert(token, valueType);
                field.SetValue(target.Instance, value);
            }
            else
            {
                var property = (PropertyInfo)member;
                ValidateProperty(property);
                var setter = property.GetSetMethod(true);
                if (setter == null)
                    throw new InvalidOperationException("属性没有 setter：" + property.Name);
                valueType = property.PropertyType;
                value = _converter.Convert(token, valueType);
                setter.Invoke(target.Instance, new[] { value });
            }

            // 返回交给字段或 setter 的值，不额外调用 getter；需要确认实际状态时由脚本显式 get。
            var result = Value(value, valueType);
            result["assigned"] = true;
            return result;
        }

        private JObject Create(JObject request)
        {
            var type = ResolveType(RequiredString(request, "type"));
            if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters || type.IsPointer || type.IsByRef)
                throw new NotSupportedException("该类型不能直接构造：" + TypeName(type));
            if (typeof(UnityObject).IsAssignableFrom(type))
                throw new NotSupportedException("UnityEngine.Object 不能通过 C# 构造函数创建；请 invoke 该类型现有的工厂方法。");
            var arguments = ReadArguments(request);
            var parameterTypes = ReadParameterTypes(request, arguments.Count);
            var constructors = type.GetConstructors(DeclaredMembers).Where(item => !item.IsStatic).Cast<MethodBase>();

            // 没有显式无参构造函数的结构体仍可取得默认值；之后遵循普通句柄和 set 规则。
            if (type.IsValueType && arguments.Count == 0 && !constructors.Any(item => item.GetParameters().Length == 0))
                return Value(Activator.CreateInstance(type), type);

            var constructor = (ConstructorInfo)SelectMethod(constructors, arguments.Count, parameterTypes, TypeName(type));
            var converted = ConvertArguments(arguments, constructor.GetParameters());
            return Value(constructor.Invoke(converted), type);
        }

        private JObject Invoke(JObject request)
        {
            var target = ReadTarget(request);
            var name = RequiredString(request, "member");
            var arguments = ReadArguments(request);
            var parameterTypes = ReadParameterTypes(request, arguments.Count);
            var signatures = new HashSet<string>(StringComparer.Ordinal);
            var methods = new List<MethodBase>();
            foreach (var type in Hierarchy(target.Type, ReadDeclaringType(request, target.Type)))
            {
                foreach (var method in type.GetMethods(DeclaredMembers))
                {
                    if (method.Name == name && method.IsStatic == target.IsStatic && signatures.Add(MethodKey(method)))
                        methods.Add(method);
                }
            }

            var selected = (MethodInfo)SelectMethod(methods, arguments.Count, parameterTypes, name);
            var converted = ConvertArguments(arguments, selected.GetParameters());
            var value = selected.Invoke(target.Instance, converted);
            return Value(value, selected.ReturnType);
        }

        private JObject Release(JObject request)
        {
            if (request.TryGetValue("all", out var allToken) && allToken.Type != JTokenType.Boolean)
                throw new ArgumentException("all 必须是布尔值。");
            if ((bool?)allToken == true)
            {
                if (request["targets"] != null)
                    throw new ArgumentException("all 与 targets 只能提供一种。");
                var count = _objects.Count;
                Clear();
                return new JObject { ["released"] = count };
            }

            if (!(request["targets"] is JArray targets))
                throw new ArgumentException("release 必须提供 targets 数组或 all: true。");
            var handles = targets.Select(ReadHandleToken).Distinct(StringComparer.Ordinal).ToArray();
            // 先校验整批句柄，再释放，避免一条错误请求只完成一部分。
            foreach (var handle in handles)
            {
                if (!_objects.ContainsKey(handle))
                    throw new KeyNotFoundException("不存在或已释放的句柄：" + handle);
            }
            foreach (var handle in handles)
            {
                _handles.Remove(_objects[handle]);
                _objects.Remove(handle);
            }
            return new JObject { ["released"] = handles.Length };
        }

        private MemberInfo FindValueMember(JObject request, Target target)
        {
            var name = RequiredString(request, "member");
            var kind = OptionalString(request, "kind") ?? "field";
            if (kind != "field" && kind != "property")
                throw new ArgumentException("kind 只能是 field 或 property；默认只访问字段。");

            foreach (var type in Hierarchy(target.Type, ReadDeclaringType(request, target.Type)))
            {
                if (kind == "field")
                {
                    var field = type.GetField(name, DeclaredMembers);
                    if (field != null && field.IsStatic == target.IsStatic)
                        return field;
                }
                else
                {
                    var properties = type.GetProperties(DeclaredMembers)
                        .Where(item => item.Name == name && IsStatic(item) == target.IsStatic).ToArray();
                    if (properties.Length > 1)
                        throw new AmbiguousMatchException("存在多个同名属性；索引器不受支持：" + name);
                    if (properties.Length == 1)
                        return properties[0];
                }
            }
            throw new MissingMemberException("未找到" + (target.IsStatic ? "静态" : "实例") + " " + kind + "：" + name);
        }

        private Target ReadTarget(JObject request)
        {
            var hasTarget = request["target"] != null;
            var hasType = request["type"] != null;
            if (hasTarget == hasType)
                throw new ArgumentException("必须且只能提供 target（实例句柄）或 type（静态成员类型）。");
            if (hasType)
                return new Target(ResolveType(RequiredString(request, "type")), null);
            var instance = ResolveHandle(ReadHandleToken(request["target"]));
            return new Target(instance.GetType(), instance);
        }

        private Type ReadDeclaringType(JObject request, Type targetType)
        {
            var name = OptionalString(request, "declaringType");
            if (name == null)
                return null;
            var type = ResolveType(name);
            if (!type.IsAssignableFrom(targetType))
                throw new ArgumentException("declaringType 必须是目标类型或其基类/接口：" + name);
            return type;
        }

        private static IEnumerable<Type> Hierarchy(Type type, Type declaringType)
        {
            if (declaringType != null)
            {
                yield return declaringType;
                yield break;
            }
            for (var current = type; current != null; current = current.BaseType)
                yield return current;
        }

        private static MethodBase SelectMethod(IEnumerable<MethodBase> methods, int argumentCount, Type[] parameterTypes, string name)
        {
            var candidates = methods.Where(method => method.GetParameters().Length == argumentCount).ToArray();
            if (parameterTypes != null)
            {
                candidates = candidates.Where(method => method.GetParameters()
                    .Select(parameter => parameter.ParameterType).SequenceEqual(parameterTypes)).ToArray();
            }
            if (candidates.Length == 0)
                throw new MissingMethodException("没有匹配的方法/构造函数：" + name + "。参数数量必须一致；parameterTypes 如提供则必须精确匹配。");
            if (candidates.Length > 1)
                throw new AmbiguousMatchException("方法/构造函数有多个候选：" + name + "。请通过 inspect 查看签名，再提供完整 parameterTypes；不会根据 JSON 值猜测重载。");
            var reason = UnsupportedReason(candidates[0]);
            if (reason != null)
                throw new NotSupportedException(reason + "：" + Signature(candidates[0]));
            return candidates[0];
        }

        private object[] ConvertArguments(JArray arguments, ParameterInfo[] parameters)
        {
            var values = new object[arguments.Count];
            for (var index = 0; index < arguments.Count; index++)
            {
                try
                {
                    values[index] = _converter.Convert(arguments[index], parameters[index].ParameterType);
                }
                catch (Exception exception) when (exception is ArgumentException || exception is FormatException || exception is OverflowException)
                {
                    throw new ArgumentException("参数 " + index + "（" + parameters[index].Name + "）转换失败：" + exception.Message, exception);
                }
            }
            return values;
        }

        private Type[] ReadParameterTypes(JObject request, int argumentCount)
        {
            if (!request.TryGetValue("parameterTypes", out var token))
                return null;
            if (!(token is JArray names) || names.Count != argumentCount)
                throw new ArgumentException("parameterTypes 必须是与 args 长度相同的类型名称数组。");
            return names.Select(name =>
            {
                if (name.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)name))
                    throw new ArgumentException("parameterTypes 的每一项必须是非空类型名称。");
                return ResolveType((string)name);
            }).ToArray();
        }

        private static JArray ReadArguments(JObject request)
        {
            if (!request.TryGetValue("args", out var token))
                return new JArray();
            return token as JArray ?? throw new ArgumentException("args 必须是 JSON 数组。");
        }

        private JObject Value(object value, Type declaredType)
        {
            var runtimeType = value?.GetType() ?? declaredType;
            JToken encoded;
            if (value == null)
                encoded = JValue.CreateNull();
            else if (runtimeType.IsEnum)
                encoded = new JValue(Enum.Format(runtimeType, value, "G"));
            else if (IsScalar(runtimeType))
                encoded = new JValue(value is char character ? character.ToString() : value);
            else
                encoded = Reference(value);

            var result = new JObject { ["value"] = encoded, ["type"] = runtimeType == null ? null : TypeName(runtimeType) };
            if (value != null && runtimeType.IsValueType && !IsScalar(runtimeType) && !runtimeType.IsEnum)
                result["isValueTypeCopy"] = true;
            return result;
        }

        private JObject Reference(object value)
        {
            EnsureAlive(value);
            if (!_handles.TryGetValue(value, out var handle))
            {
                if (_objects.Count >= MaxHandles)
                    throw new InvalidOperationException("会话已达到 4096 个对象句柄，请先 release 不再使用的句柄。");
                handle = "o" + _nextHandle++;
                _handles.Add(value, handle);
                _objects.Add(handle, value);
            }
            return new JObject { ["$ref"] = handle, ["type"] = TypeName(value.GetType()) };
        }

        private object ResolveHandle(string handle)
        {
            if (!_objects.TryGetValue(handle, out var value))
                throw new KeyNotFoundException("不存在或已释放的句柄：" + handle);
            EnsureAlive(value);
            return value;
        }

        private static void EnsureAlive(object value)
        {
            if (value is UnityObject unityObject && unityObject == null)
                throw new InvalidOperationException("句柄引用的 Unity 对象已销毁，句柄已过期。");
        }

        private static bool IsSceneObject(UnityObject value)
        {
            var gameObject = value as GameObject;
            if (gameObject == null && value is Component component)
                gameObject = component.gameObject;
            return gameObject != null && gameObject.scene.IsValid() && gameObject.scene.isLoaded;
        }

        private static string UnityObjectId(UnityObject value)
        {
            // Unity 6000.5 将 GetInstanceID 标记为编译错误；仅在这一处适配引擎标识 API 的变化。
#if UNITY_6000_5_OR_NEWER
            return value.GetEntityId().ToString();
#else
            return value.GetInstanceID().ToString(CultureInfo.InvariantCulture);
#endif
        }

        private static Type ResolveType(string name)
        {
            // 只解析已经加载的程序集；外部请求不能通过类型名称触发额外程序集加载。
            var resolved = new HashSet<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(name, false, false);
                if (type != null)
                    resolved.Add(type);
            }
            if (resolved.Count == 0 && name.IndexOf(',') >= 0)
            {
                var exact = Type.GetType(name, ResolveLoadedAssembly, null, false);
                if (exact != null)
                    resolved.Add(exact);
            }
            if (resolved.Count == 0)
                throw new TypeLoadException("已加载程序集中未找到类型：" + name + "。请提供完整类型名或程序集限定名，并确认构建裁剪未移除该类型。");
            if (resolved.Count > 1)
                throw new AmbiguousMatchException("多个程序集含有同名类型，请提供 assemblyQualifiedName：" + name);
            return resolved.First();
        }

        private static Assembly ResolveLoadedAssembly(AssemblyName name)
        {
            return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly =>
                AssemblyName.ReferenceMatchesDefinition(name, assembly.GetName()));
        }

        private static IEnumerable<Type> LoadedTypes()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    types = exception.Types;
                }
                foreach (var type in types)
                {
                    if (type != null)
                        yield return type;
                }
            }
        }

        private static JObject DescribeField(FieldInfo field)
        {
            return new JObject
            {
                ["kind"] = "field",
                ["name"] = field.Name,
                ["declaringType"] = TypeName(field.DeclaringType),
                ["type"] = TypeName(field.FieldType),
                ["isStatic"] = field.IsStatic,
                ["isPublic"] = field.IsPublic,
                ["isReadOnly"] = field.IsInitOnly || field.IsLiteral,
                ["signature"] = TypeName(field.FieldType) + " " + TypeName(field.DeclaringType) + "." + field.Name
            };
        }

        private static JObject DescribeProperty(PropertyInfo property)
        {
            var getter = property.GetGetMethod(true);
            var setter = property.GetSetMethod(true);
            return new JObject
            {
                ["kind"] = "property",
                ["name"] = property.Name,
                ["declaringType"] = TypeName(property.DeclaringType),
                ["type"] = TypeName(property.PropertyType),
                ["isStatic"] = IsStatic(property),
                ["canRead"] = getter != null,
                ["canWrite"] = setter != null,
                ["getterIsPublic"] = getter != null && getter.IsPublic,
                ["setterIsPublic"] = setter != null && setter.IsPublic,
                ["isReadOnly"] = setter == null,
                ["isIndexer"] = property.GetIndexParameters().Length != 0,
                ["signature"] = TypeName(property.PropertyType) + " " + TypeName(property.DeclaringType) + "." + property.Name
            };
        }

        private static JObject DescribeMethod(MethodBase method)
        {
            var parameters = new JArray();
            foreach (var parameter in method.GetParameters())
            {
                parameters.Add(new JObject
                {
                    ["name"] = parameter.Name,
                    ["type"] = TypeName(parameter.ParameterType),
                    ["isOut"] = parameter.IsOut,
                    ["isOptional"] = parameter.IsOptional
                });
            }
            return new JObject
            {
                ["kind"] = method is ConstructorInfo ? "constructor" : "method",
                ["name"] = method.Name,
                ["declaringType"] = TypeName(method.DeclaringType),
                ["returnType"] = method is MethodInfo info ? TypeName(info.ReturnType) : TypeName(method.DeclaringType),
                ["isStatic"] = method.IsStatic,
                ["isPublic"] = method.IsPublic,
                ["parameters"] = parameters,
                ["signature"] = Signature(method),
                ["unsupportedReason"] = UnsupportedReason(method)
            };
        }

        private static string Signature(MethodBase method)
        {
            var arguments = string.Join(", ", method.GetParameters()
                .Select(parameter => TypeName(parameter.ParameterType) + " " + parameter.Name));
            var returnType = method is MethodInfo info ? TypeName(info.ReturnType) + " " : string.Empty;
            return returnType + TypeName(method.DeclaringType) + "." + method.Name + "(" + arguments + ")";
        }

        private static string MethodKey(MethodInfo method)
        {
            return method.Name + "`" + method.GetGenericArguments().Length + "(" + string.Join(",", method.GetParameters()
                .Select(parameter => parameter.ParameterType.AssemblyQualifiedName ?? parameter.ParameterType.Name)) + ")";
        }

        private static string UnsupportedReason(MethodBase method)
        {
            if (method.ContainsGenericParameters)
                return "不支持开放泛型方法或泛型声明类型";
            if ((method.CallingConvention & CallingConventions.VarArgs) != 0)
                return "不支持可变参数调用约定";
            if (method.GetParameters().Any(parameter => parameter.IsOut || parameter.ParameterType.IsByRef || parameter.ParameterType.IsPointer))
                return "不支持 ref、out 或指针参数";
            if (method is MethodInfo info && (info.ReturnType.IsByRef || info.ReturnType.IsPointer))
                return "不支持 ref 或指针返回值";
            return null;
        }

        private static bool IsAccessor(MethodInfo method)
        {
            return method.IsSpecialName && (method.Name.StartsWith("get_", StringComparison.Ordinal) ||
                method.Name.StartsWith("set_", StringComparison.Ordinal) || method.Name.StartsWith("add_", StringComparison.Ordinal) ||
                method.Name.StartsWith("remove_", StringComparison.Ordinal));
        }

        private static bool IsStatic(PropertyInfo property)
        {
            return (property.GetGetMethod(true) ?? property.GetSetMethod(true)).IsStatic;
        }

        private static void ValidateProperty(PropertyInfo property)
        {
            if (property.GetIndexParameters().Length != 0)
                throw new NotSupportedException("get/set 不支持索引器；请 inspect 后显式 invoke 相应访问方法。");
            if (property.PropertyType.IsByRef || property.PropertyType.IsPointer)
                throw new NotSupportedException("不支持 ref 或指针属性：" + property.Name);
        }

        private static object Call(Func<object> action)
        {
            try
            {
                return action();
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                var cause = exception.InnerException;
                while (cause is TargetInvocationException nested && nested.InnerException != null)
                    cause = nested.InnerException;
                ExceptionDispatchInfo.Capture(cause).Throw();
                throw;
            }
        }

        private static string ReadHandleToken(JToken token)
        {
            if (token is JObject reference)
                token = reference["$ref"];
            if (token == null || token.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)token))
                throw new ArgumentException("句柄必须是非空字符串或 {\"$ref\":\"o1\"}。");
            return (string)token;
        }

        private static string RequiredString(JObject request, string name)
        {
            return OptionalString(request, name) ?? throw new ArgumentException("缺少必填字符串：" + name);
        }

        private static string OptionalString(JObject request, string name)
        {
            if (!request.TryGetValue(name, out var token))
                return null;
            if (token.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)token))
                throw new ArgumentException(name + " 必须是非空字符串。");
            return (string)token;
        }

        private static Page ReadPage(JObject request)
        {
            var offset = ReadInteger(request, "offset", 0);
            var limit = ReadInteger(request, "limit", 50);
            if (offset < 0 || limit < 1 || limit > 200)
                throw new ArgumentOutOfRangeException(nameof(request), "offset 必须大于等于 0，limit 必须在 1 到 200 之间。");
            return new Page(offset, limit);
        }

        private static int ReadInteger(JObject request, string name, int fallback)
        {
            if (!request.TryGetValue(name, out var token))
                return fallback;
            if (token.Type != JTokenType.Integer)
                throw new ArgumentException(name + " 必须是整数。");
            return checked((int)token);
        }

        private static JObject Paged(JArray items, int total, Page page)
        {
            return new JObject { ["items"] = items, ["total"] = total, ["offset"] = page.Offset, ["limit"] = page.Limit };
        }

        private static bool IsScalar(Type type)
        {
            return type == typeof(string) || type == typeof(bool) || type == typeof(char) ||
                   type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
                   type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
                   type == typeof(float) || type == typeof(double) || type == typeof(decimal);
        }

        private static string TypeName(Type type)
        {
            return type.FullName ?? type.Name;
        }

        private readonly struct Target
        {
            public readonly Type Type;
            public readonly object Instance;
            public bool IsStatic => Instance == null;

            public Target(Type type, object instance)
            {
                Type = type;
                Instance = instance;
            }
        }

        private readonly struct Page
        {
            public readonly int Offset;
            public readonly int Limit;

            public Page(int offset, int limit)
            {
                Offset = offset;
                Limit = limit;
            }
        }

        private sealed class ReferenceIdentityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceIdentityComparer Instance = new ReferenceIdentityComparer();
            public new bool Equals(object left, object right) => ReferenceEquals(left, right);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
