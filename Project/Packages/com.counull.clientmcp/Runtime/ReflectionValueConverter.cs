using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ClientMcp
{
    /// <summary>
    /// 将 JSON 转为反射签名已经明确的类型。只处理显式输入，不序列化对象、不遍历 getter，
    /// 也不根据 $type 加载类型；复杂引用必须来自会话句柄或由目标类型明确的数据结构。
    /// </summary>
    internal sealed class ReflectionValueConverter
    {
        private readonly Func<string, object> _resolveHandle;

        public ReflectionValueConverter(Func<string, object> resolveHandle)
        {
            _resolveHandle = resolveHandle;
        }

        public object Convert(JToken token, Type targetType)
        {
            if (targetType.IsByRef || targetType.IsPointer || targetType.ContainsGenericParameters)
                throw new NotSupportedException("不支持该参数类型：" + targetType.FullName);
            var nullableType = Nullable.GetUnderlyingType(targetType);
            if (token == null || token.Type == JTokenType.Null)
            {
                if (targetType.IsValueType && nullableType == null)
                    throw new ArgumentException("非可空值类型不能接收 null：" + targetType.FullName);
                return null;
            }
            if (nullableType != null)
                return Convert(token, nullableType);

            if (token is JObject reference && reference.TryGetValue("$ref", out var referenceToken))
                return ConvertReference(reference, referenceToken, targetType);
            if (typeof(JToken).IsAssignableFrom(targetType))
            {
                if (!targetType.IsInstanceOfType(token))
                    throw new ArgumentException("JSON 形状与目标 JToken 类型不一致。");
                return token.DeepClone();
            }
            if (token is JValue scalar)
                return ConvertScalar(scalar, targetType);
            if (token is JArray array)
                return ConvertArray(array, targetType);
            if (token is JObject data)
                return ConvertObject(data, targetType);
            throw new ArgumentException("不支持的 JSON 参数形状：" + token.Type);
        }

        private object ConvertReference(JObject reference, JToken token, Type targetType)
        {
            if (token.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)token) ||
                reference.Properties().Any(property => property.Name != "$ref" && property.Name != "type"))
                throw new ArgumentException("对象引用格式应为 {\"$ref\":\"o1\"}，可携带返回值中的 type 元数据。");
            var value = _resolveHandle((string)token);
            if (!targetType.IsInstanceOfType(value))
                throw new ArgumentException("句柄对象类型 " + value.GetType().FullName + " 不能赋给 " + targetType.FullName);
            return value;
        }

        private static object ConvertScalar(JValue token, Type targetType)
        {
            if (targetType == typeof(object))
                return token.Value;
            if (targetType == typeof(string))
            {
                if (token.Type != JTokenType.String)
                    throw new ArgumentException("字符串参数必须使用 JSON 字符串。");
                return (string)token;
            }
            if (targetType == typeof(bool))
            {
                if (token.Type != JTokenType.Boolean)
                    throw new ArgumentException("布尔参数必须使用 JSON true/false。");
                return (bool)token;
            }
            if (targetType == typeof(char))
            {
                if (token.Type != JTokenType.String || ((string)token).Length != 1)
                    throw new ArgumentException("字符参数必须是长度为 1 的 JSON 字符串。");
                return ((string)token)[0];
            }
            if (targetType.IsEnum)
            {
                if (token.Type == JTokenType.String)
                    return Enum.Parse(targetType, (string)token, false);
                if (token.Type != JTokenType.Integer)
                    throw new ArgumentException("枚举参数必须是枚举名称或整数。");
                return Enum.ToObject(targetType, ConvertNumber(token, Enum.GetUnderlyingType(targetType)));
            }
            if (IsNumeric(targetType))
                return ConvertNumber(token, targetType);
            throw new ArgumentException("JSON 标量不能赋给 " + targetType.FullName + "；请先 create 或使用对象句柄。");
        }

        private static object ConvertNumber(JValue token, Type targetType)
        {
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
                throw new ArgumentException("数值参数必须使用 JSON 数值。");
            if (targetType != typeof(float) && targetType != typeof(double) && targetType != typeof(decimal))
            {
                var number = System.Convert.ToDecimal(token.Value, CultureInfo.InvariantCulture);
                if (decimal.Truncate(number) != number)
                    throw new ArgumentException("小数不能隐式取整，请传入整数。");
            }
            var converted = System.Convert.ChangeType(token.Value, targetType, CultureInfo.InvariantCulture);
            if ((converted is float single && (float.IsNaN(single) || float.IsInfinity(single))) ||
                (converted is double number64 && (double.IsNaN(number64) || double.IsInfinity(number64))))
                throw new ArgumentOutOfRangeException(nameof(token), "数值必须是目标类型范围内的有限数。");
            return converted;
        }

        private object ConvertArray(JArray token, Type targetType)
        {
            if (targetType.IsArray)
            {
                if (targetType.GetArrayRank() != 1)
                    throw new NotSupportedException("JSON 数组只支持一维数组。");
                var elementType = targetType.GetElementType();
                var array = Array.CreateInstance(elementType, token.Count);
                for (var index = 0; index < token.Count; index++)
                    array.SetValue(Convert(token[index], elementType), index);
                return array;
            }

            // 接口只落到标准 List<T>，不为未知集合猜测构造或 Add 语义。
            var listType = GetSupportedGeneric(targetType, typeof(List<>), typeof(IList<>), typeof(ICollection<>), typeof(IEnumerable<>), typeof(IReadOnlyList<>), typeof(IReadOnlyCollection<>));
            if (listType == null)
                throw new ArgumentException("JSON 数组需要一维数组、List<T> 或其标准集合接口，当前为 " + targetType.FullName);
            var itemType = listType.GetGenericArguments()[0];
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType));
            foreach (var item in token)
                list.Add(Convert(item, itemType));
            return list;
        }

        private object ConvertObject(JObject token, Type targetType)
        {
            if (token.Property("$type") != null)
                throw new ArgumentException("不接受 $type；参数类型只能来自反射签名或已存在的句柄。");
            if (targetType == typeof(object) || targetType.IsInterface || targetType.IsAbstract)
            {
                var dictionaryType = GetSupportedGeneric(targetType, typeof(IDictionary<,>), typeof(IReadOnlyDictionary<,>));
                if (dictionaryType != null)
                    return ConvertDictionary(token, dictionaryType);
                throw new ArgumentException("JSON 对象需要明确的可构造类型；object、接口和抽象类型请使用对象句柄。");
            }
            var concreteDictionary = GetSupportedGeneric(targetType, typeof(Dictionary<,>));
            if (concreteDictionary != null)
                return ConvertDictionary(token, concreteDictionary);
            if (typeof(UnityEngine.Object).IsAssignableFrom(targetType))
                throw new ArgumentException("UnityEngine.Object 参数必须使用现有对象句柄。");
            if (!targetType.IsValueType && targetType.GetConstructor(Type.EmptyTypes) == null)
                throw new ArgumentException("类型没有公开无参构造函数，请先 create 后传入句柄：" + targetType.FullName);

            // 仅为输入中逐个指明的公开字段/属性赋值，未出现的成员不会被读取或写入。
            // 因此 Vector3 等结构体可以直接接收 {x: 1, y: 2, z: 3}，同时不触发其计算属性 getter。
            var instance = Activator.CreateInstance(targetType);
            foreach (var property in token.Properties())
                AssignPublicMember(instance, targetType, property.Name, property.Value);
            return instance;
        }

        private object ConvertDictionary(JObject token, Type dictionaryType)
        {
            var arguments = dictionaryType.GetGenericArguments();
            if (arguments[0] != typeof(string))
                throw new NotSupportedException("JSON 对象只支持字符串键字典。");
            var dictionary = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(arguments));
            foreach (var property in token.Properties())
                dictionary.Add(property.Name, Convert(property.Value, arguments[1]));
            return dictionary;
        }

        private void AssignPublicMember(object instance, Type targetType, string name, JToken token)
        {
            var field = targetType.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field != null)
            {
                if (field.IsInitOnly || field.IsLiteral)
                    throw new ArgumentException("JSON 不能写入只读字段：" + name);
                field.SetValue(instance, Convert(token, field.FieldType));
                return;
            }
            var property = targetType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            var setter = property?.GetSetMethod(false);
            if (setter == null || property.GetIndexParameters().Length != 0)
                throw new ArgumentException("JSON 成员不是公开可写字段/属性：" + targetType.FullName + "." + name);
            setter.Invoke(instance, new[] { Convert(token, property.PropertyType) });
        }

        private static Type GetSupportedGeneric(Type targetType, params Type[] definitions)
        {
            return targetType.IsGenericType && definitions.Contains(targetType.GetGenericTypeDefinition()) ? targetType : null;
        }

        private static bool IsNumeric(Type type)
        {
            return type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
                   type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
                   type == typeof(float) || type == typeof(double) || type == typeof(decimal);
        }
    }
}
