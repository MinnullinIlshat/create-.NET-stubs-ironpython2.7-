using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

public static class StubGenerator
{
    // === НАСТРОЙКА: какие namespace'ы нам нужны ===
    private static readonly HashSet<string> TargetNamespaces = new HashSet<string>
    {
        "ТвойNamespace",           // ← замени на свои
        "ДругойNamespace",
        // Добавь все нужные
    };

    public static string[] GenerateStubs()
    {
        var result = new List<string>();
        result.Add("// === СТАБЫ СГЕНЕРИРОВАНЫ АВТОМАТИЧЕСКИ ===");
        result.Add("// Файлы будут распределены по папкам Stubs/<Namespace>/");
        result.Add("");

        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic &&
                        !a.FullName.StartsWith("System.") &&
                        !a.FullName.StartsWith("Microsoft.") &&
                        !a.FullName.StartsWith("mscorlib"));

        foreach (var asm in assemblies.OrderBy(a => a.FullName))
        {
            foreach (var type in asm.GetTypes()
                .Where(t => t.IsPublic)
                .OrderBy(t => t.FullName))
            {
                if (!ShouldIncludeType(type)) 
                    continue;

                try
                {
                    string code = GenerateTypeStub(type);
                    result.AddRange(code.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));
                    result.Add("");
                }
                catch (Exception ex)
                {
                    result.Add($"// ОШИБКА: {type.FullName} - {ex.Message}");
                }
            }
        }

        return result.ToArray();
    }

    private static bool ShouldIncludeType(Type type)
    {
        if (TargetNamespaces.Count == 0) return true;
        return TargetNamespaces.Any(ns => 
            type.Namespace?.StartsWith(ns, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static string GenerateTypeStub(Type type)
    {
        var sb = new StringBuilder();

        // === Добавляем using'и ===
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using System.Text;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine();

        // Namespace
        if (!string.IsNullOrEmpty(type.Namespace))
            sb.AppendLine($"namespace {type.Namespace};");
        else
            sb.AppendLine("namespace Stubs;");

        sb.AppendLine();

        // Заголовок типа
        string modifiers = GetTypeModifiers(type);
        string kind = GetTypeKind(type);
        string typeName = GetTypeName(type);

        sb.Append($"{modifiers}{kind} {typeName}");

        // Базовый класс + интерфейсы
        var inheritance = GetInheritance(type);
        if (inheritance.Count > 0)
            sb.Append($" : {string.Join(", ", inheritance)}");

        sb.AppendLine();
        sb.AppendLine("{");

        // === Генерация членов ===
        GenerateMembers(type, sb);

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GetTypeModifiers(Type type)
    {
        var mods = new List<string> { "public" };

        if (type.IsAbstract && !type.IsInterface) mods.Add("abstract");
        if (type.IsSealed && !type.IsEnum) mods.Add("sealed");

        return string.Join(" ", mods) + " ";
    }

    private static string GetTypeKind(Type type)
    {
        if (type.IsInterface) return "interface";
        if (type.IsEnum) return "enum";
        if (type.IsValueType) return "struct";
        return "class";
    }

    private static string GetTypeName(Type type)
    {
        if (type.IsGenericType)
        {
            var args = string.Join(", ", type.GetGenericArguments().Select(t => t.Name));
            return $"{type.Name.Split('`')[0]}<{args}>";
        }
        return type.Name;
    }

    private static List<string> GetInheritance(Type type)
    {
        var list = new List<string>();

        if (type.BaseType != null && type.BaseType != typeof(object))
            list.Add(GetTypeName(type.BaseType));

        foreach (var iface in type.GetInterfaces())
            list.Add(GetTypeName(iface));

        return list;
    }

    private static void GenerateMembers(Type type, StringBuilder sb)
    {
        // Методы
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (method.IsSpecialName) continue;

            string staticMod = method.IsStatic ? "static " : "";
            string returnType = GetTypeName(method.ReturnType);
            var parameters = method.GetParameters()
                .Select(p => $"{GetTypeName(p.ParameterType)} {p.Name}");

            sb.AppendLine($"    public {staticMod}{returnType} {method.Name}({string.Join(", ", parameters)})");
            sb.AppendLine("    {");
            sb.AppendLine("        throw new NotImplementedException();");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // Свойства
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            string staticMod = prop.GetAccessors().Any(a => a.IsStatic) ? "static " : "";
            string propType = GetTypeName(prop.PropertyType);

            sb.AppendLine($"    public {staticMod}{propType} {prop.Name} {{ get; set; }}");
        }

        // События (опционально)
        foreach (var ev in type.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            string eventType = GetTypeName(ev.EventHandlerType);
            sb.AppendLine($"    public event {eventType} {ev.Name};");
        }
    }
}