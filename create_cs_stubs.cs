using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

public static class StubGenerator
{
    private static readonly HashSet<string> TargetNamespaces = new HashSet<string>
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

    public static string[] GenerateStubs()
    {
        var result = new List<string>();

        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic &&
                        !a.FullName.StartsWith("System.") &&
                        !a.FullName.StartsWith("Microsoft.") &&
                        !a.FullName.StartsWith("mscorlib"));

        foreach (var asm in assemblies.OrderBy(a => a.FullName))
        {
            foreach (var type in asm.GetTypes().Where(t => t.IsPublic).OrderBy(t => t.FullName))
            {
                if (!ShouldInclude(type)) continue;

                try
                {
                    string code = GenerateTypeStub(type);
                    result.AddRange(code.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));
                    result.Add("");
                }
                catch (Exception ex)
                {
                    result.Add($"// ОШИБКА: {type.FullName} → {ex.Message}");
                }
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
        string modifiers = GetModifiers(type);
        string kind = GetKind(type);
        string name = GetFriendlyTypeName(type);

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

        // Базовый класс и интерфейсы
        if (type.BaseType != null && type.BaseType != typeof(object))
            AddNamespace(namespaces, type.BaseType);

        foreach (var iface in type.GetInterfaces())
            AddNamespace(namespaces, iface);

        // Методы
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (method.IsSpecialName) continue;

            AddNamespace(namespaces, method.ReturnType);

            foreach (var param in method.GetParameters())
                AddNamespace(namespaces, param.ParameterType);
        }

        // Свойства
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            AddNamespace(namespaces, prop.PropertyType);
        }

        // Убираем namespace самого типа
        if (!string.IsNullOrWhiteSpace(type.Namespace))
            namespaces.Remove(type.Namespace);

        // Убираем слишком общие системные (можно оставить или убрать)
        namespaces.RemoveWhere(ns => ns == "System" || ns == "System.Collections.Generic");

        return namespaces;
    }

    private static void AddNamespace(HashSet<string> set, Type type)
    {
        if (type == null) return;

        // Обрабатываем ByRef и массивы
        if (type.IsByRef || type.IsArray)
            type = type.GetElementType();

        if (type == null) return;

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

    private static string GetModifiers(Type type)
    {
        var mods = new List<string> { "public" };
        if (type.IsAbstract && !type.IsInterface) mods.Add("abstract");
        if (type.IsSealed && !type.IsEnum) mods.Add("sealed");
        return string.Join(" ", mods) + " ";
    }

    private static string GetKind(Type type)
    {
        if (type.IsInterface) return "interface";
        if (type.IsEnum) return "enum";
        if (type.IsValueType) return "struct";
        return "class";
    }

    private static string GetFriendlyTypeName(Type type)
    {
        if (type == null) return "object";
        if (type == typeof(void) || type == typeof(System.Void)) return "void";

        if (type.IsByRef) return GetFriendlyTypeName(type.GetElementType());
        if (type.IsArray) return GetFriendlyTypeName(type.GetElementType()) + "[]";

        if (type.IsGenericType)
        {
            string baseName = type.Name.Split('`')[0];
            var args = string.Join(", ", type.GetGenericArguments().Select(GetFriendlyTypeName));
            return $"{baseName}<{args}>";
        }

        // Простые типы
        if (type == typeof(string)) return "string";
        if (type == typeof(int)) return "int";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(decimal)) return "decimal";
        if (type == typeof(double)) return "double";
        if (type == typeof(object)) return "object";

        return type.Name;
    }

    private static List<string> GetInheritance(Type type)
    {
        var result = new List<string>();
        if (type.BaseType != null && type.BaseType != typeof(object))
            result.Add(GetFriendlyTypeName(type.BaseType));
        result.AddRange(type.GetInterfaces().Select(GetFriendlyTypeName));
        return result;
    }

    private static void GenerateMembers(Type type, StringBuilder sb)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (method.IsSpecialName) continue;

            string staticMod = method.IsStatic ? "static " : "";
            string returnType = GetFriendlyTypeName(method.ReturnType);
            var parameters = method.GetParameters()
                .Select(p => $"{GetFriendlyTypeName(p.ParameterType)} {p.Name}");

            sb.AppendLine($"    public {staticMod}{returnType} {method.Name}({string.Join(", ", parameters)})");
            sb.AppendLine("    {");
            sb.AppendLine("        throw new NotImplementedException();");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            string staticMod = prop.GetAccessors().Any(a => a.IsStatic) ? "static " : "";
            string propType = GetFriendlyTypeName(prop.PropertyType);
            sb.AppendLine($"    public {staticMod}{propType} {prop.Name} {{ get; set; }}");
        }
    }
}