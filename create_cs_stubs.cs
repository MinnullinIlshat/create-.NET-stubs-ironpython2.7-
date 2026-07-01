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

        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using System.Text;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(type.Namespace))
            sb.AppendLine($"namespace {type.Namespace};");
        else
            sb.AppendLine("namespace Stubs.Generated;");

        sb.AppendLine();

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

    // ====================== ГЛАВНАЯ ФУНКЦИЯ ======================
    private static string GetFriendlyTypeName(Type type)
    {
        if (type == null) return "object";

        // void
        if (type == typeof(void) || type == typeof(System.Void))
            return "void";

        // ByRef (ref / out параметры)
        if (type.IsByRef)
        {
            return GetFriendlyTypeName(type.GetElementType());
        }

        // Массивы
        if (type.IsArray)
        {
            return GetFriendlyTypeName(type.GetElementType()) + "[]";
        }

        // Generic типы
        if (type.IsGenericType)
        {
            string baseName = type.Name.Split('`')[0];
            var args = string.Join(", ", type.GetGenericArguments().Select(GetFriendlyTypeName));
            return $"{baseName}<{args}>";
        }

        // Простые типы (для красоты)
        if (type == typeof(string)) return "string";
        if (type == typeof(int)) return "int";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(decimal)) return "decimal";
        if (type == typeof(double)) return "double";
        if (type == typeof(float)) return "float";
        if (type == typeof(long)) return "long";
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