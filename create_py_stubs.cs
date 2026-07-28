using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

public static class PythonStubGenerator
{
    // === НАСТРОЙКА: какие namespace'ы генерировать ===
    private static readonly HashSet<string> TargetNamespaces = new HashSet<string>
    {
        "Avanpost",
        "APServices",
        "ApplicationServices"
        // Добавь свои
    };

    public static string[] GenerateStubs()
    {
        var result = new List<string>();
        result.Add("# === PYTHON STUBS GENERATED AUTOMATICALLY ===");
        result.Add("# Файлы будут разложены в typings/<Namespace>/<Class>.pyi");
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
                    string code = GeneratePythonTypeStub(type);
                    result.AddRange(code.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));
                    result.Add("");
                    result.Add("# === END OF TYPE ===");
                    result.Add("");
                }
                catch (Exception ex)
                {
                    result.Add($"# ERROR: {type.FullName} → {ex.Message}");
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

    private static string GeneratePythonTypeStub(Type type)
    {
        var sb = new StringBuilder();

        // Собираем все нужные импорты
        var imports = CollectImports(type);

        // Группируем импорты
        var typingImports = imports.Where(i => i.StartsWith("typing.")).Select(i => i.Substring(7)).Distinct().OrderBy(x => x).ToList();
        var systemImports = imports.Where(i => i.StartsWith("System.")).Select(i => i.Substring(7)).Distinct().OrderBy(x => x).ToList();
        var otherImports = imports.Where(i => !i.StartsWith("typing.") && !i.StartsWith("System.")).Distinct().OrderBy(x => x).ToList();

        // Пишем импорты
        if (typingImports.Count > 0)
            sb.AppendLine($"from typing import {string.Join(", ", typingImports)}");

        if (systemImports.Count > 0)
            sb.AppendLine($"from System import {string.Join(", ", systemImports)}");

        foreach (var other in otherImports)
            sb.AppendLine($"from {other} import *");   // или более точечно, если нужно

        if (typingImports.Count + systemImports.Count + otherImports.Count > 0)
            sb.AppendLine();

        // Комментарий с namespace (для будущего Python-парсера)
        if (!string.IsNullOrWhiteSpace(type.Namespace))
            sb.AppendLine($"# namespace: {type.Namespace}");

        // Заголовок класса
        string className = GetPythonTypeName(type, forClassName: true);
        sb.AppendLine($"class {className}:");

        // Члены
        GenerateMembers(type, sb);

        // Если членов нет — добавляем pass
        if (!HasAnyMembers(type))
            sb.AppendLine("    pass");

        return sb.ToString();
    }

    // ==================== СБОР ИМПОРТОВ ====================
    private static HashSet<string> CollectImports(Type type)
    {
        var imports = new HashSet<string>();

        // Всегда полезные
        imports.Add("typing.Any");
        imports.Add("typing.Optional");

        // Базовый класс и интерфейсы
        if (type.BaseType != null && type.BaseType != typeof(object))
            AddTypeImport(imports, type.BaseType);

        foreach (var iface in type.GetInterfaces())
            AddTypeImport(imports, iface);

        // Методы
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (method.IsSpecialName) continue;
            AddTypeImport(imports, method.ReturnType);
            foreach (var p in method.GetParameters())
                AddTypeImport(imports, p.ParameterType);
        }

        // Свойства
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            AddTypeImport(imports, prop.PropertyType);

        return imports;
    }

    // ==================== СБОР ИМПОРТОВ ====================
    private static void AddTypeImport(HashSet<string> imports, Type type)
    {
        if (type == null) return;

        if (type.IsByRef || type.IsArray)
            type = type.GetElementType();

        if (type == null) return;

        // Простые типы и None — импорты не нужны
        if (type == typeof(void) || type == typeof(System.Void) ||
            type == typeof(string) || type == typeof(int) || type == typeof(bool) ||
            type == typeof(double) || type == typeof(float) || type == typeof(decimal) ||
            type == typeof(long) || type == typeof(object))
            return;

        if (type.IsGenericType)
        {
            var genDef = type.GetGenericTypeDefinition();

            // Nullable<T> → T | None  →  никаких импортов
            if (genDef == typeof(Nullable<>))
            {
                // Только рекурсивно для T
                foreach (var arg in type.GetGenericArguments())
                    AddTypeImport(imports, arg);
                return;
            }

            if (genDef == typeof(Dictionary<,>))
            {
                imports.Add("System.Dictionary");
            }
            else if (genDef == typeof(List<>) || genDef == typeof(IList<>) || genDef == typeof(ICollection<>))
            {
                // list[...] — встроенный тип, импорт не нужен
            }
            else if (genDef == typeof(IEnumerable<>) ||
                    genDef == typeof(IReadOnlyList<>) ||
                    genDef == typeof(IReadOnlyCollection<>))
            {
                imports.Add("typing.Iterable");
            }
            else if (genDef.FullName?.StartsWith("System.Collections.Generic") == true)
            {
                imports.Add($"System.{genDef.Name.Split('`')[0]}");
            }

            foreach (var arg in type.GetGenericArguments())
                AddTypeImport(imports, arg);

            return;
        }

        // System-типы, которые оставляем как есть
        if (type.Namespace != null && type.Namespace.StartsWith("System"))
        {
            imports.Add($"System.{type.Name}");
            return;
        }
    }

    // ==================== ИМЯ ТИПА ДЛЯ PYTHON ====================
    private static string GetPythonTypeName(Type type, bool forClassName = false)
    {
        if (type == null) return "Any";

        // void
        if (type == typeof(void))
            return "None";

        // Простые типы
        if (type == typeof(string)) return "str";
        if (type == typeof(int) || type == typeof(Int32)) return "int";
        if (type == typeof(bool) || type == typeof(Boolean)) return "bool";
        if (type == typeof(double) || type == typeof(Double)) return "float";
        if (type == typeof(float) || type == typeof(Single)) return "float";
        if (type == typeof(decimal)) return "float";
        if (type == typeof(long) || type == typeof(Int64)) return "int";
        if (type == typeof(object)) return "Any";

        // ByRef
        if (type.IsByRef)
            return GetPythonTypeName(type.GetElementType());

        // Массивы
        if (type.IsArray)
            return $"list[{GetPythonTypeName(type.GetElementType())}]";

        // Generic
        if (type.IsGenericType)
        {
            var genDef = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments().Select(t => GetPythonTypeName(t)).ToArray();

            // === Nullable<T> → T | None  (современный синтаксис) ===
            if (genDef == typeof(Nullable<>))
                return $"{args[0]} | None";

            // Dictionary
            if (genDef == typeof(Dictionary<,>))
                return $"Dictionary[{args[0]}, {args[1]}]";

            // List / IList / ICollection
            if (genDef == typeof(List<>) || genDef == typeof(IList<>) || genDef == typeof(ICollection<>))
                return $"list[{args[0]}]";

            // IEnumerable / IReadOnlyList → Iterable
            if (genDef == typeof(IEnumerable<>) ||
                genDef == typeof(IReadOnlyList<>) ||
                genDef == typeof(IReadOnlyCollection<>))
                return $"Iterable[{args[0]}]";

            // Остальные generic
            string baseName = genDef.Name.Split('`')[0];
            return $"{baseName}[{string.Join(", ", args)}]";
        }

        // Для имени самого класса
        if (forClassName)
            return type.Name.Split('`')[0];

        return type.Name.Split('`')[0];
    }

    private static bool HasAnyMembers(Type type)
    {
        return type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                   .Any(m => !m.IsSpecialName)
            || type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Any();
    }

    // ==================== ГЕНЕРАЦИЯ ЧЛЕНОВ ====================
    private static void GenerateMembers(Type type, StringBuilder sb)
    {
        // Методы
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            // === Фильтр невалидных имён ===
            if (method.IsSpecialName) continue;
            if (method.Name.Contains('<') || method.Name.Contains('>') || method.Name.Contains('$'))
                continue;
            if (method.Name.StartsWith("get_") || method.Name.StartsWith("set_") || method.Name.StartsWith("add_") || method.Name.StartsWith("remove_"))
                continue;

            string returnType = GetPythonTypeName(method.ReturnType);
            var parameters = method.GetParameters()
                .Select(p => $"{SanitizeParamName(p.Name)}: {GetPythonTypeName(p.ParameterType)}")
                .ToList();

            if (method.IsStatic)
            {
                sb.AppendLine("    @staticmethod");
                sb.AppendLine($"    def {method.Name}({string.Join(", ", parameters)}) -> {returnType}:");
            }
            else
            {
                var allParams = new List<string> { "self" };
                allParams.AddRange(parameters);
                sb.AppendLine($"    def {method.Name}({string.Join(", ", allParams)}) -> {returnType}:");
            }

            sb.AppendLine("        ...");
            sb.AppendLine();
        }

        // Свойства
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            // Пропускаем свойства с невалидными именами
            if (prop.Name.Contains('<') || prop.Name.Contains('>') || prop.Name.Contains('$'))
                continue;

            string propType = GetPythonTypeName(prop.PropertyType);

            if (prop.GetAccessors().Any(a => a.IsStatic))
            {
                sb.AppendLine($"    {prop.Name}: {propType}");
            }
            else
            {
                sb.AppendLine("    @property");
                sb.AppendLine($"    def {prop.Name}(self) -> {propType}:");
                sb.AppendLine("        ...");
            }
            sb.AppendLine();
        }
    }

    private static string SanitizeParamName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "arg";
        // Python не любит некоторые имена
        if (name == "from") return "from_";
        if (name == "class") return "class_";
        if (name == "def") return "def_";
        return name;
    }
}