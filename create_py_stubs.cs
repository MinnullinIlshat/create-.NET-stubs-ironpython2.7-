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

        // === Собираем импорты ===
        var imports = CollectImports(type);

        // === Собираем TypeVar'ы, которые реально используются ===
        var typeVars = CollectTypeVars(type);

        // --- Запись импортов ---
        var typingImports = new SortedSet<string>();
        var systemImports = new SortedSet<string>();
        var projectImports = new SortedSet<string>();

        foreach (var imp in imports)
        {
            if (imp.StartsWith("typing."))
                typingImports.Add(imp.Substring(7));
            else if (imp.StartsWith("System."))
                systemImports.Add(imp.Substring(7));
            else if (imp.StartsWith("project:"))
                projectImports.Add(imp.Substring(8));
        }

        // TypeVar и Generic всегда нужны, если есть generic-параметры
        if (typeVars.Count > 0)
        {
            typingImports.Add("TypeVar");
            typingImports.Add("Generic");
        }

        if (typingImports.Count > 0)
            sb.AppendLine($"from typing import {string.Join(", ", typingImports)}");

        if (systemImports.Count > 0)
            sb.AppendLine($"from System import {string.Join(", ", systemImports)}");

        foreach (var full in projectImports)
        {
            int lastDot = full.LastIndexOf('.');
            if (lastDot > 0)
            {
                string module = full;
                string className = full.Substring(lastDot + 1);
                sb.AppendLine($"from {module} import {className}");
            }
        }

        if (typingImports.Count + systemImports.Count + projectImports.Count > 0)
            sb.AppendLine();

        // === TypeVar объявления ===
        foreach (var tv in typeVars.OrderBy(x => x))
        {
            sb.AppendLine($"{tv} = TypeVar(\"{tv}\")");
        }
        if (typeVars.Count > 0)
            sb.AppendLine();

        // === Namespace comment ===
        if (!string.IsNullOrWhiteSpace(type.Namespace))
            sb.AppendLine($"# namespace: {type.Namespace}");

        // === Заголовок класса (Python 3.11 стиль) ===
        string className = GetPythonTypeName(type, forClassName: true);

        if (type.IsGenericTypeDefinition || type.GetGenericArguments().Length > 0)
        {
            // class MyClass(Generic[T, TKey]):
            var genericArgs = type.GetGenericArguments()
                                .Select(t => t.Name)
                                .ToArray();

            if (genericArgs.Length > 0)
                sb.AppendLine($"class {className}(Generic[{string.Join(", ", genericArgs)}]):");
            else
                sb.AppendLine($"class {className}:");
        }
        else
        {
            sb.AppendLine($"class {className}:");
        }

        // Члены
        GenerateMembers(type, sb);

        if (!HasAnyMembers(type))
            sb.AppendLine("    pass");

        return sb.ToString();
    }

    // ==================== СБОР ИМПОРТОВ ====================
    private static HashSet<string> CollectImports(Type type)
    {
        var imports = new HashSet<string>();

        // Базовые
        imports.Add("typing.Any");

        // Базовый класс и интерфейсы
        if (type.BaseType != null && type.BaseType != typeof(object))
            AddTypeImport(imports, type.BaseType, type);

        foreach (var iface in type.GetInterfaces())
            AddTypeImport(imports, iface, type);

        // Методы
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (method.IsSpecialName) continue;
            if (method.Name.Contains('<') || method.Name.Contains('>') || method.Name.Contains('$'))
                continue;

            AddTypeImport(imports, method.ReturnType, type);

            foreach (var p in method.GetParameters())
                AddTypeImport(imports, p.ParameterType, type);
        }

        // Свойства
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (prop.Name.Contains('<') || prop.Name.Contains('>') || prop.Name.Contains('$'))
                continue;

            AddTypeImport(imports, prop.PropertyType, type);
        }

        return imports;
    }

    private static void AddTypeImport(HashSet<string> imports, Type usedType, Type currentType)
    {
        if (usedType == null) return;

        // Раскрываем ByRef и массивы
        if (usedType.IsByRef || usedType.IsArray)
            usedType = usedType.GetElementType();

        if (usedType == null) return;

        // Простые типы — пропускаем
        if (usedType == typeof(void) || usedType == typeof(System.Void) ||
            usedType == typeof(string) || usedType == typeof(int) || usedType == typeof(bool) ||
            usedType == typeof(double) || usedType == typeof(float) || usedType == typeof(decimal) ||
            usedType == typeof(long) || usedType == typeof(object))
            return;

        // ValueTuple — не импортируем
        if (usedType.FullName != null &&
            (usedType.FullName.StartsWith("System.ValueTuple") || usedType.Name.StartsWith("ValueTuple")))
            return;

        // Nullable — рекурсивно только для T
        if (usedType.IsGenericType && usedType.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            foreach (var arg in usedType.GetGenericArguments())
                AddTypeImport(imports, arg, currentType);
            return;
        }

        // Generic System-коллекции
        if (usedType.IsGenericType)
        {
            var genDef = usedType.GetGenericTypeDefinition();

            if (genDef == typeof(Dictionary<,>))
            {
                imports.Add("System.Dictionary");
            }
            else if (genDef == typeof(IEnumerable<>) ||
                    genDef == typeof(IReadOnlyList<>) ||
                    genDef == typeof(IReadOnlyCollection<>))
            {
                imports.Add("typing.Iterable");
            }
            // list[...] — встроенный, импорт не нужен

            // Рекурсивно для аргументов
            foreach (var arg in usedType.GetGenericArguments())
                AddTypeImport(imports, arg, currentType);

            return;
        }

        // === System-типы ===
        if (usedType.Namespace != null && usedType.Namespace.StartsWith("System"))
        {
            imports.Add($"System.{usedType.Name}");
            return;
        }

        // === Типы из ваших namespace'ов (самое важное исправление) ===
        // Не импортируем сам себя
        if (usedType == currentType)
            return;

        if (!string.IsNullOrWhiteSpace(usedType.Namespace) && !string.IsNullOrWhiteSpace(usedType.Name))
        {
            // Формат: "project:Namespace.ClassName"
            // Потом при генерации превратим в правильный from ... import ...
            string full = $"{usedType.Namespace}.{usedType.Name.Split('`')[0]}";
            imports.Add($"project:{full}");
        }
    }

    // ==================== ИМЯ ТИПА ДЛЯ PYTHON ====================
    private static string GetPythonTypeName(Type type, bool forClassName = false)
    {
        if (type == null) return "Any";

        // Только typeof(void) — System.Void не используем
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

        // ValueTuple → tuple
        if (type.FullName != null &&
            (type.FullName.StartsWith("System.ValueTuple") || type.Name.StartsWith("ValueTuple")))
        {
            if (type.IsGenericType)
            {
                var args = type.GetGenericArguments().Select(t => GetPythonTypeName(t));
                return $"tuple[{string.Join(", ", args)}]";
            }
            return "tuple";
        }

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

            // Nullable<T> → T | None
            if (genDef == typeof(Nullable<>))
                return $"{args[0]} | None";

            if (genDef == typeof(Dictionary<,>))
                return $"Dictionary[{args[0]}, {args[1]}]";

            if (genDef == typeof(List<>) || genDef == typeof(IList<>) || genDef == typeof(ICollection<>))
                return $"list[{args[0]}]";

            if (genDef == typeof(IEnumerable<>) ||
                genDef == typeof(IReadOnlyList<>) ||
                genDef == typeof(IReadOnlyCollection<>))
                return $"Iterable[{args[0]}]";

            // Обычный generic
            string baseName = genDef.Name.Split('`')[0];
            return $"{baseName}[{string.Join(", ", args)}]";
        }

        // Generic type parameter (T, TKey, TValue...)
        if (type.IsGenericParameter)
            return type.Name; // оставим T, TKey и т.д.

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

    private static HashSet<string> CollectTypeVars(Type type)
    {
        var typeVars = new HashSet<string>();

        // Параметры самого класса
        foreach (var arg in type.GetGenericArguments())
        {
            if (arg.IsGenericParameter)
                typeVars.Add(arg.Name);
        }

        // Из методов и свойств
        void CollectFromType(Type t)
        {
            if (t == null) return;

            if (t.IsGenericParameter)
            {
                typeVars.Add(t.Name);
                return;
            }

            if (t.IsByRef || t.IsArray)
            {
                CollectFromType(t.GetElementType());
                return;
            }

            if (t.IsGenericType)
            {
                foreach (var arg in t.GetGenericArguments())
                    CollectFromType(arg);
            }
        }

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (method.IsSpecialName) continue;
            if (method.Name.Contains('<') || method.Name.Contains('>') || method.Name.Contains('$'))
                continue;

            CollectFromType(method.ReturnType);
            foreach (var p in method.GetParameters())
                CollectFromType(p.ParameterType);
        }

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (prop.Name.Contains('<') || prop.Name.Contains('>') || prop.Name.Contains('$'))
                continue;
            CollectFromType(prop.PropertyType);
        }

        return typeVars;
    }
}