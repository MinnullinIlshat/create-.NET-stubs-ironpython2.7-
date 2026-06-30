using System;
using System.Reflection;
using System.Text;
using System.Linq;
using System.Collections.Generic;

public class StubGenerator
{
    // ←←← ИЗМЕНИ ЭТО под свои нужды
    private static readonly HashSet<string> ImportantNamespaces = new HashSet<string>
    {
        "YourCompany",     // ← замени на реальные пространства имён
        "YourProject",
        "Scripts.Common",
        // Добавляй сюда нужные namespace'ы
    };

    public static string[] GenerateStubs()
    {
        var lines = new List<string>();

        lines.Add("// === СТАБЫ СГЕНЕРИРОВАНЫ " + DateTime.Now + " ===");
        lines.Add("// Скопируй этот массив в VS Code");
        lines.Add("");

        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic 
                     && !a.FullName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                     && !a.FullName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
                     && !a.FullName.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.FullName);

        foreach (var asm in assemblies)
        {
            lines.Add($"// === СБОРКА: {asm.FullName} ===");

            var types = asm.GetTypes()
                .Where(t => t.IsPublic)
                .OrderBy(t => t.FullName);

            foreach (var type in types)
            {
                // Фильтрация по namespace
                if (ImportantNamespaces.Count > 0 && 
                    !ImportantNamespaces.Any(ns => 
                        type.Namespace?.StartsWith(ns, StringComparison.OrdinalIgnoreCase) == true))
                    continue;

                try
                {
                    string typeCode = GenerateTypeStub(type);
                    lines.AddRange(typeCode.Split(new[] { Environment.NewLine }, StringSplitOptions.None));
                    lines.Add(""); // пустая строка между типами
                }
                catch (Exception ex)
                {
                    lines.Add($"// Ошибка при генерации {type.FullName}: {ex.Message}");
                }
            }
        }

        lines.Add("// === КОНЕЦ ГЕНЕРАЦИИ ===");
        return lines.ToArray();
    }

    private static string GenerateTypeStub(Type type)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(type.Namespace))
            sb.AppendLine($"namespace {type.Namespace};");

        sb.AppendLine();

        string kind = type.IsInterface ? "interface" :
                      type.IsEnum ? "enum" :
                      type.IsValueType ? "struct" : "class";

        string modifiers = "public ";
        if (type.IsAbstract && !type.IsInterface) modifiers += "abstract ";
        if (type.IsSealed && !type.IsEnum) modifiers += "sealed ";

        sb.Append($"{modifiers}{kind} {GetTypeShortName(type)}");

        // Базовый класс + интерфейсы
        var bases = new List<string>();
        if (type.BaseType != null && type.BaseType != typeof(object))
            bases.Add(GetTypeShortName(type.BaseType));

        foreach (var iface in type.GetInterfaces())
            bases.Add(GetTypeShortName(iface));

        if (bases.Count > 0)
            sb.Append($" : {string.Join(", ", bases)}");

        sb.AppendLine();
        sb.AppendLine("{");

        // Методы
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        foreach (var m in methods.Where(m => !m.IsSpecialName))
        {
            string returnType = GetTypeShortName(m.ReturnType);
            var parameters = m.GetParameters()
                .Select(p => $"{GetTypeShortName(p.ParameterType)} {p.Name}");

            string staticMod = m.IsStatic ? "static " : "";
            sb.AppendLine($"    public {staticMod}{returnType} {m.Name}({string.Join(", ", parameters)})");
            sb.AppendLine("    {");
            sb.AppendLine("        throw new System.NotImplementedException();");
            sb.AppendLine("    }");
        }

        // Свойства
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        foreach (var p in properties)
        {
            string propType = GetTypeShortName(p.PropertyType);
            string staticMod = (p.GetGetMethod()?.IsStatic == true) ? "static " : "";
            sb.AppendLine($"    public {staticMod}{propType} {p.Name} {{ get; set; }}");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GetTypeShortName(Type type)
    {
        if (type.IsGenericType)
        {
            var args = string.Join(", ", type.GetGenericArguments().Select(GetTypeShortName));
            return $"{type.Name.Split('`')[0]}<{args}>";
        }

        if (type == typeof(int)) return "int";
        if (type == typeof(string)) return "string";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(void)) return "void";
        if (type == typeof(object)) return "object";
        if (type == typeof(decimal)) return "decimal";
        if (type == typeof(DateTime)) return "DateTime";

        return type.Name;
    }
}