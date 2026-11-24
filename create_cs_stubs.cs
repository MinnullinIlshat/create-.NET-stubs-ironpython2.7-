using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

public static class StubGenerator
{
    private static readonly HashSet<string> TargetNamespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Avanpost",
        "APServices",
        "ApplicationServices"
    };

    // Базовые using'и, которые добавляются всегда
    private static readonly List<string> AlwaysIncludeUsings = new List<string>
    {
        "System",
        "System.Collections.Generic",
        "System.Linq",
        "System.Text",
        "System.Threading.Tasks"
    };

    private static readonly ConcurrentDictionary<Type, Type> SubstitutionCache =
        new ConcurrentDictionary<Type, Type>();

    public static string[] GenerateStubs()
    {
        var result = new List<string>();
        result.Add("// === C# STUBS GENERATED AUTOMATICALLY ===");
        result.Add("");

        var types = new List<Type>();

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.FullName))
        {
            if (asm.IsDynamic) continue;
            var full = asm.FullName ?? "";
            if (full.StartsWith("System.") || full.StartsWith("Microsoft.") || full.StartsWith("mscorlib"))
                continue;

            Type[] asmTypes;
            try { asmTypes = asm.GetTypes(); }
            catch { continue; }

            foreach (var type in asmTypes)
            {
                if (type == null || !type.IsPublic) continue;
                if (type.IsNested) continue;
                if (!ShouldInclude(type)) continue;

                if (type.IsInterface)
                {
                    var resolved = ResolveType(type);
                    if (resolved != null && resolved != type)
                        continue;
                }

                types.Add(type);
            }
        }

        foreach (var type in types.OrderBy(t => t.FullName))
        {
            try
            {
                string code = GenerateTypeStub(type);
                result.AddRange(code.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));
                result.Add("");
            }
            catch (Exception ex)
            {
                result.Add($"// ОШИБКА: {type.FullName} → {ex.Message}");
                result.Add("");
            }
        }

        return result.ToArray();
    }

    private static bool ShouldInclude(Type type)
    {
        if (TargetNamespaces.Count == 0) return true;
        return type.Namespace != null &&
               TargetNamespaces.Any(ns => type.Namespace.StartsWith(ns, StringComparison.OrdinalIgnoreCase));
    }

    // ========== Подмена IXxx → Xxx только если реализация существует ==========
    private static Type ResolveType(Type type)
    {
        if (type == null) return null;

        // IFoo<Bar> → сначала Foo<>, потом Foo<Bar>
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            Type def = type.GetGenericTypeDefinition();
            Type resolvedDef = ResolveType(def);
            if (resolvedDef != null && resolvedDef != def && resolvedDef.IsGenericTypeDefinition)
            {
                try
                {
                    var args = type.GetGenericArguments().Select(ResolveType).ToArray();
                    return resolvedDef.MakeGenericType(args);
                }
                catch
                {
                    return resolvedDef;
                }
            }
            return type;
        }

        if (SubstitutionCache.TryGetValue(type, out var cached))
            return cached;

        Type resolved = FindImplementation(type) ?? type;
        SubstitutionCache[type] = resolved;
        return resolved;
    }

    private static Type FindImplementation(Type iface)
    {
        if (iface == null || !iface.IsInterface)
            return null;

        string name = iface.Name.Split('`')[0];
        if (name.Length < 2 || name[0] != 'I' || !char.IsUpper(name[1]))
            return null;

        string implName = name.Substring(1); // IUnitExpressionBuilder → UnitExpressionBuilder
        string ns = iface.Namespace ?? "";
        int arity = iface.IsGenericType ? iface.GetGenericArguments().Length : 0;

        Type sameNs = null;
        Type anyNs = null;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;

            Type[] types;
            try { types = asm.GetTypes(); }
            catch { continue; }

            foreach (var t in types)
            {
                if (t == null || !t.IsClass || t.IsAbstract || !t.IsPublic)
                    continue;
                if (t.Name.Split('`')[0] != implName)
                    continue;

                int tArity = t.IsGenericType ? t.GetGenericArguments().Length : 0;
                if (tArity != arity)
                    continue;

                if (!ImplementsInterface(t, iface))
                    continue;

                if ((t.Namespace ?? "") == ns)
                {
                    sameNs = t;
                    break;
                }

                if (anyNs == null)
                    anyNs = t;
            }

            if (sameNs != null)
                break;
        }

        return sameNs ?? anyNs;
    }

    private static bool ImplementsInterface(Type impl, Type iface)
    {
        if (iface.IsAssignableFrom(impl))
            return true;

        Type ifaceDef = iface.IsGenericType ? iface.GetGenericTypeDefinition() : iface;

        Type[] ifaces;
        try { ifaces = impl.GetInterfaces(); }
        catch { return false; }

        foreach (var i in ifaces)
        {
            if (i == iface)
                return true;
            if (i.IsGenericType && i.GetGenericTypeDefinition() == ifaceDef)
                return true;
        }

        return false;
    }

    private static string GenerateTypeStub(Type type)
    {
        var sb = new StringBuilder();

        // === 1. Собираем все нужные using ===
        var requiredUsings = CollectRequiredUsings(type);

        foreach (var ns in requiredUsings.OrderBy(x => x))
        {
            sb.AppendLine($"using {ns};");
        }

        sb.AppendLine();

        // === 2. Namespace текущего типа ===
        if (!string.IsNullOrWhiteSpace(type.Namespace))
            sb.AppendLine($"namespace {type.Namespace};");
        else
            sb.AppendLine("namespace Stubs.Generated;");

        sb.AppendLine();

        // === 3. Заголовок типа ===
        string name = GetFriendlyTypeName(type);
        string classDoc = GetMemberDoc(type);

        // Делегаты объявляются отдельной конструкцией, а не как класс
        if (typeof(Delegate).IsAssignableFrom(type))
        {
            var invoke = type.GetMethod("Invoke");
            string delegateReturn = GetFriendlyTypeName(invoke.ReturnType);
            var delegateParams = invoke.GetParameters().Select(FormatParameter);
            AppendXmlDoc(sb, classDoc, 0);
            sb.AppendLine($"public delegate {delegateReturn} {name}({string.Join(", ", delegateParams)});");
            return sb.ToString();
        }

        string modifiers = GetModifiers(type);
        string kind = GetKind(type);

        AppendXmlDoc(sb, classDoc, 0);
        sb.Append($"{modifiers}{kind} {name}");

        var inheritance = GetInheritance(type);
        if (inheritance.Count > 0)
            sb.Append($" : {string.Join(", ", inheritance)}");

        sb.AppendLine();
        sb.AppendLine("{");

        GenerateMembers(type, sb);

        sb.AppendLine("}");
        return sb.ToString();
    }

    // ==================== СБОР ИСПОЛЬЗУЕМЫХ NAMESPACE ====================
    private static HashSet<string> CollectRequiredUsings(Type type)
    {
        var namespaces = new HashSet<string>(AlwaysIncludeUsings);
        var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        // Делегатам нужны только типы из сигнатуры Invoke,
        // а не наследство MulticastDelegate/ISerializable
        if (typeof(Delegate).IsAssignableFrom(type))
        {
            var invoke = type.GetMethod("Invoke");
            AddNamespace(namespaces, invoke.ReturnType);
            foreach (var param in invoke.GetParameters())
                AddNamespace(namespaces, param.ParameterType);
        }
        else
        {
            // Базовый класс и интерфейсы
            if (type.BaseType != null && type.BaseType != typeof(object))
                AddNamespace(namespaces, type.BaseType);

            foreach (var iface in type.GetInterfaces())
                AddNamespace(namespaces, iface);

            // Методы
            foreach (var method in type.GetMethods(flags).Where(IsUsableMethod))
            {
                AddNamespace(namespaces, method.ReturnType);

                foreach (var param in method.GetParameters())
                    AddNamespace(namespaces, param.ParameterType);
            }

            // Свойства (включая параметры индексаторов)
            foreach (var prop in type.GetProperties(flags).Where(IsUsableProperty))
            {
                AddNamespace(namespaces, prop.PropertyType);

                foreach (var indexParam in prop.GetIndexParameters())
                    AddNamespace(namespaces, indexParam.ParameterType);
            }
        }

        // Убираем namespace самого типа
        if (!string.IsNullOrWhiteSpace(type.Namespace))
            namespaces.Remove(type.Namespace);

        return namespaces;
    }

    private static void AddNamespace(HashSet<string> set, Type type)
    {
        if (type == null) return;

        // Обрабатываем ByRef и массивы
        if (type.IsByRef || type.IsArray)
            type = type.GetElementType();

        if (type == null || type.IsGenericParameter) return;

        type = ResolveType(type) ?? type;

        if (!string.IsNullOrWhiteSpace(type.Namespace))
            set.Add(type.Namespace);

        // Для generic типов добавляем namespace'ы аргументов
        if (type.IsGenericType)
        {
            foreach (var arg in type.GetGenericArguments())
                AddNamespace(set, arg);
        }
    }

    // ==================== ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ ====================

    private static readonly Dictionary<Type, string> PrimitiveNames = new Dictionary<Type, string>
    {
        { typeof(string), "string" },
        { typeof(object), "object" },
        { typeof(bool), "bool" },
        { typeof(byte), "byte" },
        { typeof(sbyte), "sbyte" },
        { typeof(short), "short" },
        { typeof(ushort), "ushort" },
        { typeof(int), "int" },
        { typeof(uint), "uint" },
        { typeof(long), "long" },
        { typeof(ulong), "ulong" },
        { typeof(float), "float" },
        { typeof(double), "double" },
        { typeof(decimal), "decimal" },
        { typeof(char), "char" },
    };

    private static string GetModifiers(Type type)
    {
        if (type.IsInterface || type.IsEnum)
            return "public ";

        var mods = new List<string> { "public" };

        // static class в IL выглядит как abstract + sealed
        if (type.IsAbstract && type.IsSealed)
            mods.Add("static");
        else
        {
            if (type.IsAbstract) mods.Add("abstract");
            if (type.IsSealed) mods.Add("sealed");
        }

        return string.Join(" ", mods) + " ";
    }

    private static string GetKind(Type type)
    {
        if (type.IsInterface) return "interface";
        if (type.IsEnum) return "enum";
        if (type.IsValueType) return "struct";
        return "class";
    }

    private static string GetFriendlyTypeName(Type type, bool resolve = true)
    {
        if (type == null) return "object";

        if (type.IsGenericParameter)
            return type.Name;

        if (resolve)
            type = ResolveType(type) ?? type;

        if (type == typeof(void)) return "void";

        if (type.IsByRef) return GetFriendlyTypeName(type.GetElementType());
        if (type.IsArray) return GetFriendlyTypeName(type.GetElementType()) + "[]";

        if (type.IsGenericType)
        {
            if (type.GetGenericTypeDefinition() == typeof(Nullable<>))
                return GetFriendlyTypeName(type.GetGenericArguments()[0]) + "?";

            string baseName = type.Name.Split('`')[0];
            var args = string.Join(", ", type.GetGenericArguments().Select(t => GetFriendlyTypeName(t)));
            return $"{baseName}<{args}>";
        }

        // Простые типы
        if (PrimitiveNames.TryGetValue(type, out var primitive))
            return primitive;

        return type.Name.Split('`')[0];
    }

    private static List<string> GetInheritance(Type type)
    {
        var result = new List<string>();

        // Enum/struct: базовый System.Enum/System.ValueType в объявлении не пишется
        if (type.IsEnum)
            return result;

        if (type.BaseType != null && type.BaseType != typeof(object) && type.BaseType != typeof(ValueType))
            result.Add(GetFriendlyTypeName(type.BaseType, resolve: false));

        // Подменённые интерфейсы не генерируются как стабы, поэтому из наследования их убираем.
        // Оставшиеся интерфейсы НЕ подменяем: класс объявляет свои реальные интерфейсы.
        result.AddRange(type.GetInterfaces()
            .Where(i => ResolveType(i) == i)
            .Select(i => GetFriendlyTypeName(i, resolve: false)));
        return result;
    }

    private static void GenerateMembers(Type type, StringBuilder sb)
    {
        var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        if (type.IsEnum)
        {
            GenerateEnumFields(type, sb);
            return;
        }

        foreach (var method in type.GetMethods(flags).Where(IsUsableMethod).OrderBy(m => m.Name))
        {
            string staticMod = method.IsStatic ? "static " : "";
            string returnType = GetFriendlyTypeName(method.ReturnType);
            var parameters = method.GetParameters().Select(FormatParameter);

            string genericPart = "";
            if (method.IsGenericMethodDefinition)
                genericPart = "<" + string.Join(", ", method.GetGenericArguments().Select(a => a.Name)) + ">";

            string signature = $"{staticMod}{returnType} {method.Name}{genericPart}({string.Join(", ", parameters)})";

            AppendXmlDoc(sb, GetMemberDoc(method), 4);

            if (type.IsInterface)
            {
                sb.AppendLine($"    {signature};");
            }
            else
            {
                sb.AppendLine($"    public {signature}");
                sb.AppendLine("    {");
                sb.AppendLine("        throw new NotImplementedException();");
                sb.AppendLine("    }");
            }
            sb.AppendLine();
        }

        foreach (var prop in type.GetProperties(flags).Where(IsUsableProperty).OrderBy(p => p.Name))
        {
            string staticMod = prop.GetAccessors().Any(a => a.IsStatic) ? "static " : "";
            string propType = GetFriendlyTypeName(prop.PropertyType);
            string modifier = type.IsInterface ? "" : "public ";

            AppendXmlDoc(sb, GetMemberDoc(prop), 4);

            var indexParams = prop.GetIndexParameters();
            if (indexParams.Length > 0)
            {
                string indexerParams = string.Join(", ", indexParams.Select(FormatParameter));

                if (type.IsInterface)
                {
                    sb.AppendLine($"    {propType} this[{indexerParams}] {{{BuildAccessors(prop)} }}");
                }
                else
                {
                    // Авто-индексаторов в C# нет — нужны тела аксессоров
                    sb.AppendLine($"    {modifier}{staticMod}{propType} this[{indexerParams}]");
                    sb.AppendLine("    {");
                    if (prop.CanRead) sb.AppendLine("        get { throw new NotImplementedException(); }");
                    if (prop.CanWrite) sb.AppendLine("        set { throw new NotImplementedException(); }");
                    sb.AppendLine("    }");
                }
                continue;
            }

            sb.AppendLine($"    {modifier}{staticMod}{propType} {prop.Name} {{{BuildAccessors(prop)} }}");
        }
    }

    private static string BuildAccessors(PropertyInfo prop)
    {
        var accessors = new StringBuilder();
        if (prop.CanRead) accessors.Append(" get;");
        if (prop.CanWrite) accessors.Append(" set;");
        return accessors.ToString();
    }

    private static void GenerateEnumFields(Type type, StringBuilder sb)
    {
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .ToList();

        for (int i = 0; i < fields.Count; i++)
        {
            object value = fields[i].GetRawConstantValue();
            string comma = i < fields.Count - 1 ? "," : "";
            sb.AppendLine($"    {fields[i].Name} = {value}{comma}");
        }
    }

    // ========== Фильтрация членов ==========
    private static bool IsUsableMethod(MethodInfo m)
    {
        if (m == null || m.IsSpecialName) return false;
        if (m.DeclaringType == typeof(object)) return false;
        if (m.Name.Contains('<') || m.Name.Contains('>') || m.Name.Contains('$')) return false;
        if (m.Name.StartsWith("get_") || m.Name.StartsWith("set_") ||
            m.Name.StartsWith("add_") || m.Name.StartsWith("remove_")) return false;
        return true;
    }

    private static bool IsUsableProperty(PropertyInfo p)
    {
        if (p == null) return false;
        if (p.DeclaringType == typeof(object)) return false;
        if (p.Name.Contains('<') || p.Name.Contains('>') || p.Name.Contains('$')) return false;
        return true;
    }

    // ========== Параметры ==========
    private static string FormatParameter(ParameterInfo p)
    {
        var sb = new StringBuilder();

        if (p.GetCustomAttributes(typeof(ParamArrayAttribute), false).Length > 0)
            sb.Append("params ");
        else if (p.ParameterType.IsByRef)
            sb.Append(p.IsOut ? "out " : "ref ");

        sb.Append(GetFriendlyTypeName(p.ParameterType));
        sb.Append(' ');
        sb.Append(SanitizeParamName(p.Name));
        return sb.ToString();
    }

    private static readonly HashSet<string> CSharpKeywords = new HashSet<string>
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char",
        "checked", "class", "const", "continue", "decimal", "default", "delegate", "do",
        "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int",
        "interface", "internal", "is", "lock", "long", "namespace", "new", "null",
        "object", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof",
        "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using",
        "virtual", "void", "volatile", "while"
    };

    private static string SanitizeParamName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "arg";
        return CSharpKeywords.Contains(name) ? "@" + name : name;
    }

    // ========== Документация без чтения файлов ==========
    private static string GetMemberDoc(MemberInfo member)
    {
        if (member == null) return "";

        object desc = member.GetCustomAttributes(true)
            .FirstOrDefault(a => a.GetType().Name == "DescriptionAttribute");
        if (desc != null)
        {
            var p = desc.GetType().GetProperty("Description");
            var text = p?.GetValue(desc) as string;
            if (!string.IsNullOrWhiteSpace(text))
                return text.Trim();
        }

        if (member.GetCustomAttributes(true).FirstOrDefault(a => a is ObsoleteAttribute) is ObsoleteAttribute oa &&
            !string.IsNullOrWhiteSpace(oa.Message))
            return "Obsolete: " + oa.Message;

        return "";
    }

    private static void AppendXmlDoc(StringBuilder sb, string doc, int indent)
    {
        if (string.IsNullOrWhiteSpace(doc)) return;

        string pad = new string(' ', indent);
        sb.AppendLine(pad + "/// <summary>");
        foreach (var line in doc.Replace("\r\n", "\n").Split('\n'))
            sb.AppendLine(pad + "/// " + EscapeXml(line.TrimEnd()));
        sb.AppendLine(pad + "/// </summary>");
    }

    private static string EscapeXml(string text)
    {
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}