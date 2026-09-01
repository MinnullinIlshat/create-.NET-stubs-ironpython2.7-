using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

public static class PythonStubGenerator
{
    private static readonly HashSet<string> TargetNamespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "",
    };

    private static readonly ConcurrentDictionary<Type, Type> SubstitutionCache =
        new ConcurrentDictionary<Type, Type>();

    public static string[] GenerateStubs()
    {
        var result = new List<string>();
        result.Add("# === PYTHON STUBS GENERATED AUTOMATICALLY ===");
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
                string code = GeneratePythonTypeStub(type);
                result.AddRange(code.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));
                result.Add("");
                result.Add("# === END OF TYPE ===");
                result.Add("");
            }
            catch (Exception ex)
            {
                result.Add($"# ERROR: {type.FullName} → {ex.Message}");
                result.Add("# === END OF TYPE ===");
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

    private static BindingFlags GetMemberFlags(Type type)
    {
        return BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    }

    // ========== Подмена IXxx → Xxx только если реализация существует ==========
    private static Type ResolveType(Type type)
    {
        if (type == null) return null;

        if (SubstitutionCache.TryGetValue(type, out var cached))
            return cached;

        Type resolved = type;

        if (type.IsInterface)
        {
            string name = type.Name.Split('`')[0];
            if (name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]))
            {
                string implName = name.Substring(1);
                string ns = type.Namespace ?? "";
                Type impl = null;

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.IsDynamic) continue;
                    Type[] asmTypes;
                    try { asmTypes = asm.GetTypes(); }
                    catch { continue; }

                    impl = asmTypes.FirstOrDefault(t =>
                        t.IsClass &&
                        !t.IsAbstract &&
                        t.IsPublic &&
                        t.Name.Split('`')[0] == implName &&
                        (t.Namespace ?? "") == ns &&
                        type.IsAssignableFrom(t));

                    if (impl != null) break;
                }

                if (impl != null)
                    resolved = impl;
            }
        }

        SubstitutionCache[type] = resolved;
        return resolved;
    }

    // ========== Генерация одного типа ==========
    private static string GeneratePythonTypeStub(Type type)
    {
        var sb = new StringBuilder();
        var imports = CollectImports(type);
        var typeVars = CollectTypeVars(type);
        bool hasOverloads = HasOverloads(type);

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
                systemImports.Add(imp);
            else if (imp.StartsWith("project:"))
                projectImports.Add(imp.Substring(8));
        }

        if (typeVars.Count > 0)
        {
            typingImports.Add("TypeVar");
            typingImports.Add("Generic");
        }
        if (hasOverloads)
            typingImports.Add("overload");

        if (typingImports.Count > 0)
            sb.AppendLine($"from typing import {string.Join(", ", typingImports)}");
        if (collectionsAbcImports.Count > 0)
            sb.AppendLine($"from collections.abc import {string.Join(", ", collectionsAbcImports)}");

        var fromSystem = new SortedSet<string>();
        var fromTasks = new SortedSet<string>();
        var fromExpressions = new SortedSet<string>();
        var fromCollections = new SortedSet<string>();
        var fromGeneric = new SortedSet<string>();

        var byModule = new SortedDictionary<string, SortedSet<string>>();

        foreach (var full in systemImports)
        {
            int lastDot = full.LastIndexOf('.');
            if (lastDot <= 0) continue;
            string module = full.Substring(0, lastDot);      // System.Collections.Immutable
            string name = full.Substring(lastDot + 1);       // ImmutableArray
            if (!byModule.ContainsKey(module))
                byModule[module] = new SortedSet<string>();
            byModule[module].Add(name);
        }

        foreach (var kv in byModule)
            sb.AppendLine($"from {kv.Key} import {string.Join(", ", kv.Value)}");

        foreach (var full in projectImports)
        {
            int lastDot = full.LastIndexOf('.');
            if (lastDot > 0)
            {
                string classNameImp = full.Substring(lastDot + 1);
                sb.AppendLine($"from {full} import {classNameImp}");
            }
        }

        if (typingImports.Count + collectionsAbcImports.Count + systemImports.Count + projectImports.Count > 0)
            sb.AppendLine();

        foreach (var tv in typeVars.OrderBy(x => x))
            sb.AppendLine($"{tv} = TypeVar(\"{tv}\")");
        if (typeVars.Count > 0)
            sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(type.Namespace))
            sb.AppendLine($"# namespace: {type.Namespace}");

        string className = GetPythonTypeName(type, forClassName: true);
        var bases = new List<string>();

        var genericArgs = type.GetGenericArguments()
            .Where(t => t.IsGenericParameter)
            .Select(t => t.Name)
            .ToArray();
        if (genericArgs.Length > 0)
            bases.Add($"Generic[{string.Join(", ", genericArgs)}]");

        if (bases.Count > 0)
            sb.AppendLine($"class {className}({string.Join(", ", bases)}):");
        else
            sb.AppendLine($"class {className}:");

        string classDoc = GetMemberDoc(type);
        if (!string.IsNullOrWhiteSpace(classDoc))
            sb.Append(ToPythonDocstring(classDoc, 4));

        int beforeMembers = sb.Length;
        GenerateMembers(type, sb);

        if (sb.Length == beforeMembers && string.IsNullOrWhiteSpace(classDoc))
            sb.AppendLine("    pass");

        string generated = sb.ToString();
        if (Regex.IsMatch(generated, @"\bAny\b") && !typingImports.Contains("Any"))
            typingImports.Add("Any");

        return generated;
    }

    // ========== Импорты ==========
    private static HashSet<string> CollectImports(Type type)
    {
        var imports = new HashSet<string>();
        var flags = GetMemberFlags(type);

        foreach (var method in type.GetMethods(flags).Where(IsUsableMethod))
        {
            AddTypeImport(imports, method.ReturnType, type);
            foreach (var p in method.GetParameters())
                AddTypeImport(imports, p.ParameterType, type);
        }

        foreach (var prop in type.GetProperties(flags).Where(IsUsableProperty))
            AddTypeImport(imports, prop.PropertyType, type);

        return imports;
    }

    private static void AddTypeImport(HashSet<string> imports, Type usedType, Type currentType)
    {
        if (usedType == null) return;

        while (usedType.IsByRef || usedType.IsArray || usedType.IsPointer)
        {
            usedType = usedType.GetElementType();
            if (usedType == null) return;
        }

        usedType = ResolveType(usedType) ?? usedType;

        if (usedType == typeof(void) ||
            usedType == typeof(string) || usedType == typeof(int) || usedType == typeof(bool) ||
            usedType == typeof(double) || usedType == typeof(float) || usedType == typeof(decimal) ||
            usedType == typeof(long) || usedType == typeof(byte) ||
            usedType == typeof(short) || usedType == typeof(uint) || usedType == typeof(ulong) ||
            usedType == typeof(char) || usedType == typeof(sbyte))
            return;

        if (usedType == typeof(object))
        {
            imports.Add("typing.Any");
            return;
        }

        if (usedType.IsGenericParameter) return;

        if (usedType.FullName != null &&
            (usedType.FullName.StartsWith("System.ValueTuple") || usedType.Name.StartsWith("ValueTuple")))
            return;

        if (usedType.IsGenericType && usedType.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            foreach (var arg in usedType.GetGenericArguments())
                AddTypeImport(imports, arg, currentType);
            return;
        }

        if (usedType.IsGenericType)
        {
            var genDef = usedType.GetGenericTypeDefinition();
            string genName = genDef.Name.Split('`')[0];
            string ns = genDef.Namespace ?? "";

            if (ns.StartsWith("System.Threading.Tasks") && genName == "Task")
                imports.Add("System.Threading.Tasks.Task");
            else if (ns.StartsWith("System.Linq.Expressions") && genName == "Expression")
                imports.Add("System.Linq.Expressions.Expression");
            else if (genName == "Func" || genName == "Action")
                imports.Add("collections.abc.Callable");
            else if (genDef == typeof(Dictionary<,>) || genDef == typeof(IDictionary<,>))
            {
                imports.Add("System.Collections.Generic.Dictionary");
                imports.Add("System.Collections.Generic.IDictionary");
            }
            else if (genDef == typeof(IEnumerable<>) ||
                     genDef == typeof(IReadOnlyList<>) ||
                     genDef == typeof(IReadOnlyCollection<>) ||
                     genDef == typeof(IEnumerator<>))
                imports.Add("collections.abc.Iterable");
            else if (genDef == typeof(List<>) || genDef == typeof(IList<>) || genDef == typeof(ICollection<>))
            {
                // list[...] — встроенный
            }
            else if (ns.StartsWith("System.Collections.Generic"))
                imports.Add($"System.Collections.Generic.{genName}");
            else if (ns.StartsWith("System"))
                imports.Add($"{ns}.{genName}");
            else if (!string.IsNullOrEmpty(ns) && genDef != currentType && usedType.GetGenericTypeDefinition() != currentType)
                imports.Add($"project:{ns}.{genName}");

            foreach (var arg in usedType.GetGenericArguments())
                AddTypeImport(imports, arg, currentType);
            return;
        }

        if (usedType.Namespace != null && usedType.Namespace.StartsWith("System"))
        {
            string ns = usedType.Namespace;
            string name = usedType.Name.Split('`')[0];

            if (usedType == typeof(System.Collections.IEnumerable))
                imports.Add("collections.abc.Iterable");
            else if (ns.StartsWith("System.Threading.Tasks"))
                imports.Add($"System.Threading.Tasks.{name}");
            else if (ns.StartsWith("System.Linq.Expressions"))
                imports.Add($"System.Linq.Expressions.{name}");
            else if (ns.StartsWith("System.Collections.Generic"))
                imports.Add($"System.Collections.Generic.{name}");
            else if (ns.StartsWith("System.Collections"))
                imports.Add($"{usedType.Namespace}.{usedType.Name.Split('`')[0]}");
            else
                imports.Add($"System.{name}");
            return;
        }

        if (usedType == currentType) return;

        string typeNamespace = usedType.Namespace;
        string typeName = usedType.Name.Split('`')[0];
        if (typeName.Contains('+'))
            typeName = typeName.Substring(typeName.LastIndexOf('+') + 1);

        if (string.IsNullOrWhiteSpace(typeNamespace) || string.IsNullOrWhiteSpace(typeName))
        {
            if (!string.IsNullOrEmpty(usedType.FullName))
            {
                string fullName = usedType.FullName.Split('`')[0].Split(',')[0].Trim();
                int lastDot = fullName.LastIndexOf('.');
                if (lastDot > 0)
                {
                    typeNamespace = fullName.Substring(0, lastDot);
                    typeName = fullName.Substring(lastDot + 1);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(typeNamespace) && !string.IsNullOrWhiteSpace(typeName))
            imports.Add($"project:{typeNamespace}.{typeName}");
    }

    // ========== Имена типов ==========
    private static string GetPythonTypeName(Type type, bool forClassName = false)
    {
        if (type == null) return "Any";

        if (!forClassName)
            type = ResolveType(type) ?? type;

        if (type == typeof(void))
            return "None";

        if (type == typeof(string)) return "str";
        if (type == typeof(int) || type == typeof(Int32)) return "int";
        if (type == typeof(bool) || type == typeof(Boolean)) return "bool";
        if (type == typeof(double) || type == typeof(Double)) return "float";
        if (type == typeof(float) || type == typeof(Single)) return "float";
        if (type == typeof(decimal)) return "float";
        if (type == typeof(long) || type == typeof(Int64)) return "int";
        if (type == typeof(object)) return "Any";
        if (type == typeof(byte) || type == typeof(short) || type == typeof(uint) || type == typeof(ulong))
            return "int";

        if (type.IsGenericParameter)
            return type.Name;

        if (type.FullName != null &&
            (type.FullName.StartsWith("System.ValueTuple") || type.Name.StartsWith("ValueTuple")))
        {
            if (type.IsGenericType)
                return $"tuple[{string.Join(", ", type.GetGenericArguments().Select(t => GetPythonTypeName(t)))}]";
            return "tuple";
        }

        if (type.IsByRef)
            return GetPythonTypeName(type.GetElementType());

        if (type.IsArray)
            return $"list[{GetPythonTypeName(type.GetElementType())}]";

        if (type.IsGenericType)
        {
            var genDef = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments().Select(t => GetPythonTypeName(t)).ToArray();

            if (genDef == typeof(Nullable<>))
                return $"{args[0]} | None";

            if (genDef.FullName != null && genDef.FullName.StartsWith("System.Threading.Tasks.Task"))
                return args.Length == 0 ? "Task" : $"Task[{args[0]}]";

            if (genDef.FullName != null && genDef.FullName.StartsWith("System.Linq.Expressions.Expression"))
                return args.Length == 0 ? "Expression" : $"Expression[{args[0]}]";

            if (genDef.Name.StartsWith("Func`") || genDef.Name == "Func")
            {
                if (args.Length == 0) return "Callable[[], Any]";
                if (args.Length == 1) return $"Callable[[], {args[0]}]";
                return $"Callable[[{string.Join(", ", args.Take(args.Length - 1))}], {args[args.Length - 1]}]";
            }

            if (genDef.Name.StartsWith("Action`") || genDef.Name == "Action")
            {
                if (args.Length == 0) return "Callable[[], None]";
                return $"Callable[[{string.Join(", ", args)}], None]";
            }

            if (genDef == typeof(Dictionary<,>) || genDef == typeof(IDictionary<,>))
                return $"Dictionary[{args[0]}, {args[1]}]";

            if (genDef == typeof(List<>) || genDef == typeof(IList<>) || genDef == typeof(ICollection<>))
                return $"list[{args[0]}]";

            if (genDef == typeof(IEnumerable<>) ||
                genDef == typeof(IReadOnlyList<>) ||
                genDef == typeof(IReadOnlyCollection<>))
                return $"Iterable[{args[0]}]";

            string baseName = genDef.Name.Split('`')[0];
            return $"{baseName}[{string.Join(", ", args)}]";
        }

        if (type == typeof(System.Collections.IDictionary))
            return "IDictionary";
        if (type == typeof(System.Collections.IEnumerable))
            return "Iterable";

        if (forClassName)
        {
            string name = type.Name.Split('`')[0];
            int bracket = name.IndexOf('[');
            if (bracket >= 0) name = name.Substring(0, bracket);
            return name;
        }

        return type.Name.Split('`')[0];
    }

    // ========== Члены ==========
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

    private static bool HasOverloads(Type type)
    {
        return type.GetMethods(GetMemberFlags(type))
            .Where(IsUsableMethod)
            .GroupBy(m => m.Name)
            .Any(g => g.Count() > 1);
    }

    private static bool HasAnyMembers(Type type)
    {
        var flags = GetMemberFlags(type);
        return type.GetMethods(flags).Any(IsUsableMethod) ||
               type.GetProperties(flags).Any(IsUsableProperty);
    }

    private static HashSet<string> CollectTypeVars(Type type)
    {
        var typeVars = new HashSet<string>();

        void CollectFromType(Type t)
        {
            if (t == null) return;
            if (t.IsGenericParameter) { typeVars.Add(t.Name); return; }
            if (t.IsByRef || t.IsArray) { CollectFromType(t.GetElementType()); return; }
            if (t.IsGenericType)
                foreach (var arg in t.GetGenericArguments())
                    CollectFromType(arg);
        }

        foreach (var arg in type.GetGenericArguments())
            if (arg.IsGenericParameter)
                typeVars.Add(arg.Name);

        var flags = GetMemberFlags(type);
        foreach (var method in type.GetMethods(flags).Where(IsUsableMethod))
        {
            CollectFromType(method.ReturnType);
            foreach (var p in method.GetParameters())
                CollectFromType(p.ParameterType);
        }
        foreach (var prop in type.GetProperties(flags).Where(IsUsableProperty))
            CollectFromType(prop.PropertyType);

        return typeVars;
    }

    private static void GenerateMembers(Type type, StringBuilder sb)
    {
        var flags = GetMemberFlags(type);

        var methods = type.GetMethods(flags)
            .Where(IsUsableMethod)
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

                string methodDoc = GetMemberDoc(method);
                if (!string.IsNullOrWhiteSpace(methodDoc))
                    sb.Append(ToPythonDocstring(methodDoc, 8));

                sb.AppendLine("        ...");
                sb.AppendLine();
            }
        }

        foreach (var prop in type.GetProperties(flags).Where(IsUsableProperty))
        {
            string propType = GetPythonTypeName(prop.PropertyType);
            string propDoc = GetMemberDoc(prop);

            if (prop.GetAccessors(false).Any(a => a.IsStatic))
            {
                sb.AppendLine($"    {prop.Name}: {propType}");
                if (!string.IsNullOrWhiteSpace(propDoc))
                    sb.Append(ToPythonDocstring(propDoc, 4));
            }
            else
            {
                sb.AppendLine("    @property");
                sb.AppendLine($"    def {prop.Name}(self) -> {propType}:");
                if (!string.IsNullOrWhiteSpace(propDoc))
                    sb.Append(ToPythonDocstring(propDoc, 8));
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
            "lambda" => "lambda_",
            "None" => "none_",
            "True" => "true_",
            "False" => "false_",
            _ => name
        };
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

    private static string ToPythonDocstring(string text, int indent)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string pad = new string(' ', indent);
        string escaped = text.Replace("\\", "\\\\").Replace("\"\"\"", "\\\"\\\"\\\"");
        if (!escaped.Contains('\n'))
            return pad + "\"\"\"" + escaped + "\"\"\"\n";

        var sb = new StringBuilder();
        sb.AppendLine(pad + "\"\"\"");
        foreach (var line in escaped.Replace("\r\n", "\n").Split('\n'))
            sb.AppendLine(pad + line.TrimEnd());
        sb.AppendLine(pad + "\"\"\"");
        return sb.ToString();
    }
}