using System;
using System.Reflection;
using System.Text;
using System.Linq;
using System.Collections.Generic;

public class StubGenerator
{
    // ←←← НАСТРОЙКА: укажи нужные namespace'ы здесь (очень важно!)
    private static readonly HashSet<string> ImportantNamespaces = new HashSet<string>
    {
        "YourCompany",      // ← замени на реальные
        "YourProject.Core",
        "Scripts",          // для общих скриптов
        // Добавь все важные пространства имён
    };

    public static void GenerateAndPrintStubs()
    {
        var sb = new StringBuilder();
        sb.AppendLine("// === СТАБЫ СГЕНЕРИРОВАНЫ " + DateTime.Now + " ===");
        sb.AppendLine("// Скопируй этот вывод в VS Code");
        sb.AppendLine();

        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !a.FullName.StartsWith("System.") &&
                        !a.FullName.StartsWith("Microsoft.") &&
                        !a.FullName.StartsWith("mscorlib"));

        foreach (var asm in assemblies.OrderBy(a => a.FullName))
        {
            sb.AppendLine($"// === СБОРКА: {asm.FullName} ===");

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
                    sb.AppendLine(typeCode);
                    sb.AppendLine();
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"// Ошибка при генерации {type.FullName}: {ex.Message}");
                }
            }
        }

        Console.WriteLine(sb.ToString());
        Console.WriteLine("// === КОНЕЦ ГЕНЕРАЦИИ ===");
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

        // Наследование и интерфейсы
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
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance |
                                     BindingFlags.Static | BindingFlags.DeclaredOnly);
        foreach (var m in methods)
        {
            if (m.IsSpecialName) continue; // пропускаем get_/set_

            string returnType = GetTypeShortName(m.ReturnType);
            var parameters = m.GetParameters()
                .Select(p => $"{GetTypeShortName(p.ParameterType)} {p.Name}")
                .ToList();

            string staticMod = m.IsStatic ? "static " : "";
            sb.AppendLine($"    public {staticMod}{returnType} {m.Name}({string.Join(", ", parameters)})");
            sb.AppendLine("    {");
            sb.AppendLine("        throw new System.NotImplementedException();");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // Свойства
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance |
                                             BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            string propType = GetTypeShortName(p.PropertyType);
            string staticMod = p.GetGetMethod()?.IsStatic == true ? "static " : "";
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

        // Простые типы
        if (type == typeof(int)) return "int";
        if (type == typeof(string)) return "string";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(void)) return "void";
        if (type == typeof(object)) return "object";

        return type.Name;
    }
}