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
        var collectionsAbcImports = new SortedSet<string>();
        var systemImports = new SortedSet<string>();
        var projectImports = new SortedSet<string>();

        foreach (var imp in imports)
        {
            if (imp.StartsWith("typing."))
                typingImports.Add(imp.Substring(7));
            else if (imp.StartsWith("collections.abc."))
                collectionsAbcImports.Add(imp.Substring("collections.abc.".Length));
            else if (imp.StartsWith("System."))
                systemImports.Add(imp.Substring(7)); // может содержать Collections.Generic.XXX
            else if (imp.StartsWith("project:"))
                projectImports.Add(imp.Substring(8));
        }

        // TypeVar + Generic + overload
        if (typeVars.Count > 0)
        {
            typingImports.Add("TypeVar");
            typingImports.Add("Generic");
        }
        typingImports.Add("overload"); // почти всегда полезно

        if (typingImports.Count > 0)
            sb.AppendLine($"from typing import {string.Join(", ", typingImports)}");

        if (collectionsAbcImports.Count > 0)
            sb.AppendLine($"from collections.abc import {string.Join(", ", collectionsAbcImports)}");

        // System-импорты (с правильными путями)
        var plainSystem = new SortedSet<string>();
        var genericSystem = new SortedSet<string>();
        var collectionsSystem = new SortedSet<string>();

        foreach (var s in systemImports)
        {
            if (s.StartsWith("Collections.Generic."))
                genericSystem.Add(s.Substring("Collections.Generic.".Length));
            else if (s.StartsWith("Collections."))
                collectionsSystem.Add(s.Substring("Collections.".Length));
            else
                plainSystem.Add(s);
        }

        if (plainSystem.Count > 0)
            sb.AppendLine($"from System import {string.Join(", ", plainSystem)}");
        if (collectionsSystem.Count > 0)
            sb.AppendLine($"from System.Collections import {string.Join(", ", collectionsSystem)}");
        if (genericSystem.Count > 0)
            sb.AppendLine($"from System.Collections.Generic import {string.Join(", ", genericSystem)}");

        foreach (var full in projectImports)
        {
            int lastDot = full.LastIndexOf('.');
            if (lastDot > 0)
            {
                string module = full;
                string _className = full.Substring(lastDot + 1);
                sb.AppendLine($"from {module} import {_className}");
            }
        }

        if (typingImports.Count + collectionsAbcImports.Count + systemImports.Count + projectImports.Count > 0)
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

        if (usedType.IsByRef || usedType.IsArray)
            usedType = usedType.GetElementType();

        if (usedType == null) return;

        // Простые типы
        if (usedType == typeof(void) ||
            usedType == typeof(string) || usedType == typeof(int) || usedType == typeof(bool) ||
            usedType == typeof(double) || usedType == typeof(float) || usedType == typeof(decimal) ||
            usedType == typeof(long) || usedType == typeof(object))
            return;

        // ValueTuple
        if (usedType.FullName != null &&
            (usedType.FullName.StartsWith("System.ValueTuple") || usedType.Name.StartsWith("ValueTuple")))
            return;

        // Nullable
        if (usedType.IsGenericType && usedType.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            foreach (var arg in usedType.GetGenericArguments())
                AddTypeImport(imports, arg, currentType);
            return;
        }

        if (usedType.IsGenericType)
        {
            var genDef = usedType.GetGenericTypeDefinition();

            if (genDef == typeof(Dictionary<,>) || genDef == typeof(IDictionary<,>))
            {
                imports.Add("System.Collections.Generic.Dictionary");
                // IDictionary тоже может понадобиться
                imports.Add("System.Collections.Generic.IDictionary");
            }
            else if (genDef == typeof(IEnumerable<>) ||
                    genDef == typeof(IReadOnlyList<>) ||
                    genDef == typeof(IReadOnlyCollection<>))
            {
                // Iterable из collections.abc
                imports.Add("collections.abc.Iterable");
            }

            foreach (var arg in usedType.GetGenericArguments())
                AddTypeImport(imports, arg, currentType);

            return;
        }

        // Не-generic IDictionary
        if (usedType == typeof(System.Collections.IDictionary))
        {
            imports.Add("System.Collections.IDictionary");
            return;
        }

        // System-типы
        if (usedType.Namespace != null && usedType.Namespace.StartsWith("System"))
        {
            // Более точный путь
            if (usedType.Namespace.StartsWith("System.Collections.Generic"))
                imports.Add($"System.Collections.Generic.{usedType.Name.Split('`')[0]}");
            else if (usedType.Namespace.StartsWith("System.Collections"))
                imports.Add($"System.Collections.{usedType.Name.Split('`')[0]}");
            else
                imports.Add($"System.{usedType.Name.Split('`')[0]}");
            return;
        }

        // Собственные типы проекта
        if (usedType == currentType) return;

        if (!string.IsNullOrWhiteSpace(usedType.Namespace) && !string.IsNullOrWhiteSpace(usedType.Name))
        {
            string full = $"{usedType.Namespace}.{usedType.Name.Split('`')[0]}";
            imports.Add($"project:{full}");
        }
    }

    // ==================== ИМЯ ТИПА ДЛЯ PYTHON ====================
    private static string GetPythonTypeName(Type type, bool forClassName = false)
    {
        if (type == null) return "Any";

        // Только typeof(void)
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

            // Dictionary / IDictionary
            if (genDef == typeof(Dictionary<,>) || genDef == typeof(IDictionary<,>))
                return $"Dictionary[{args[0]}, {args[1]}]";

            // List / IList / ICollection
            if (genDef == typeof(List<>) || genDef == typeof(IList<>) || genDef == typeof(ICollection<>))
                return $"list[{args[0]}]";

            // IEnumerable / IReadOnlyList → Iterable
            if (genDef == typeof(IEnumerable<>) ||
                genDef == typeof(IReadOnlyList<>) ||
                genDef == typeof(IReadOnlyCollection<>))
                return $"Iterable[{args[0]}]";

            string baseName = genDef.Name.Split('`')[0];
            return $"{baseName}[{string.Join(", ", args)}]";
        }

        // Не-generic IDictionary
        if (type == typeof(System.Collections.IDictionary))
            return "IDictionary";

        // Generic type parameter
        if (type.IsGenericParameter)
            return type.Name;

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
        // Группируем методы по имени, чтобы найти перегрузки
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Where(m => !m.Name.Contains('<') && !m.Name.Contains('>') && !m.Name.Contains('$'))
            .Where(m => !m.Name.StartsWith("get_") && !m.Name.StartsWith("set_") &&
                        !m.Name.StartsWith("add_") && !m.Name.StartsWith("remove_"))
            .GroupBy(m => m.Name)
            .ToList();

        foreach (var group in methods)
        {
            var overloads = group.ToList();
            bool hasOverloads = overloads.Count > 1;

            foreach (var method in overloads)
            {
                if (hasOverloads)
                    sb.AppendLine("    @overload");

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
        }

        // Свойства
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
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
        return name switch
        {
            "from" => "from_",
            "class" => "class_",
            "def" => "def_",
            "import" => "import_",
            "global" => "global_",
            _ => name
        };
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