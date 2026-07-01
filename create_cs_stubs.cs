using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

public static class StubGenerator
{
    private static readonly HashSet<string> TargetNamespaces = new HashSet<string>
    {
        "Avanpost"
    };

    public static string[] GenerateStubs()
    {
        var result = new List<string>();
        result.Add("// === СТАБЫ СГЕНЕРИРОВАНЫ АВТОМАТИЧЕСКИ ===");
        result.Add("");

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

        // Using'и
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using System.Text;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine();

        // Namespace
        if (!string.IsNullOrWhiteSpace(type.Namespace))
            sb.AppendLine($"namespace {type.Namespace};");
        else
            sb.AppendLine("namespace Stubs.Generated;");

        sb.AppendLine();

        // Заголовок типа
        string modifiers = GetModifiers(type);
        string kind = GetKind(type);
        string name = GetCleanTypeName(type);

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

    // === ГЛАВНОЕ ИСПРАВЛЕНИЕ: убираем `1, `2 и т.д. ===
    private static string GetCleanTypeName(Type type)
    {
        if (type.IsGenericType)
        {
            string baseName = type.Name.Split('`')[0];
            var args = string.Join(", ", type.GetGenericArguments().Select(GetCleanTypeName));
            return $"{baseName}<{args}>";
        }

        return type.Name;
    }

    private static List<string> GetInheritance(Type type)
    {
        var result = new List<string>();

        if (type.BaseType != null && type.BaseType != typeof(object))
            result.Add(GetCleanTypeName(type.BaseType));

        result.AddRange(type.GetInterfaces().Select(GetCleanTypeName));
        return result;
    }

    // === ИСПРАВЛЕНИЕ: void вместо Void ===
    private static string GetCleanTypeNameForMember(Type type)
    {
        if (type == typeof(void))
            return "void";

        if (type == typeof(System.Void))
            return "void";

        return GetCleanTypeName(type);
    }

    private static void GenerateMembers(Type type, StringBuilder sb)
    {
        // Методы
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (method.IsSpecialName) continue;

            string staticMod = method.IsStatic ? "static " : "";
            string returnType = GetCleanTypeNameForMember(method.ReturnType);

            var parameters = method.GetParameters()
                .Select(p => $"{GetCleanTypeNameForMember(p.ParameterType)} {p.Name}");

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
            string propType = GetCleanTypeNameForMember(prop.PropertyType);

            sb.AppendLine($"    public {staticMod}{propType} {prop.Name} {{ get; set; }}");
        }
    }
}