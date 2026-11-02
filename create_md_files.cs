using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

public static class AssemblyApiDocumenter
{
    /// <summary>
    /// Генерирует документацию по публичному API сборок.
    /// Возвращает список строк Markdown.
    /// Если сборок > 1 — перед каждой секцией добавляется маркер "# newfile: Имя.md".
    /// </summary>
    /// <param name="assembliesPath">Папка с .dll (опционально). Если null — только уже загруженные сборки.</param>
    public static List<string> Generate(string assembliesPath = null)
    {
        var result = new List<string>();
        var assemblies = CollectAssemblies(assembliesPath);

        var interesting = assemblies
            .Where(a => a != null && !IsSystemAssembly(a))
            .GroupBy(a => a.FullName)
            .Select(g => g.First())
            .OrderBy(a => a.GetName().Name)
            .ToList();

        if (interesting.Count == 0)
            interesting = assemblies.Where(a => a != null).Distinct().ToList();

        bool multiple = interesting.Count > 1;

        foreach (var asm in interesting)
        {
            if (multiple)
            {
                var safeName = SanitizeFileName(asm.GetName().Name);
                result.Add($"# newfile: {safeName}.md");
            }

            result.AddRange(DocumentAssembly(asm));
            result.Add(""); // пустая строка-разделитель
        }

        if (result.Count == 0)
        {
            result.Add("# Environment API");
            result.Add("");
            result.Add("Не найдено подходящих сборок для документирования.");
        }

        return result;
    }

    private static List<Assembly> CollectAssemblies(string path)
    {
        var list = new List<Assembly>();

        try
        {
            list.AddRange(AppDomain.CurrentDomain.GetAssemblies());
        }
        catch { /* ignore */ }

        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            foreach (var dll in Directory.GetFiles(path, "*.dll", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var asm = Assembly.LoadFrom(dll);
                    if (asm != null && !list.Any(a => a.FullName == asm.FullName))
                        list.Add(asm);
                }
                catch
                {
                    // пропускаем DLL, которые не удалось загрузить
                }
            }
        }

        return list;
    }

    private static bool IsSystemAssembly(Assembly asm)
    {
        var name = asm.GetName().Name ?? "";
        return name.StartsWith("System", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
            || name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase)
            || name.Equals("netstandard", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Presentation", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("WindowsBase", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> DocumentAssembly(Assembly asm)
    {
        var lines = new List<string>();
        var name = asm.GetName();

        lines.Add($"# Assembly: {name.Name}");
        lines.Add("");
        lines.Add($"**Full Name:** `{asm.FullName}`");
        lines.Add($"**Version:** {name.Version}");
        if (!string.IsNullOrEmpty(asm.Location))
            lines.Add($"**Location:** `{asm.Location}`");
        lines.Add("");

        Type[] types;
        try
        {
            types = asm.GetExportedTypes();
        }
        catch
        {
            lines.Add("> Не удалось получить экспортируемые типы (возможны зависимости).");
            lines.Add("");
            return lines;
        }

        if (types.Length == 0)
        {
            lines.Add("Нет публичных типов.");
            lines.Add("");
            return lines;
        }

        var grouped = types
            .Where(t => t != null && t.IsPublic)
            .GroupBy(t => t.Namespace ?? "(global)")
            .OrderBy(g => g.Key);

        foreach (var nsGroup in grouped)
        {
            lines.Add($"## Namespace: `{nsGroup.Key}`");
            lines.Add("");

            foreach (var type in nsGroup.OrderBy(t => t.Name))
            {
                lines.AddRange(DocumentType(type));
                lines.Add("");
            }
        }

        return lines;
    }

    private static List<string> DocumentType(Type type)
    {
        var lines = new List<string>();

        string kind = type.IsInterface ? "Interface"
            : type.IsEnum ? "Enum"
            : type.IsValueType ? "Struct"
            : type.IsClass ? "Class"
            : "Type";

        if (type.IsAbstract && type.IsSealed) kind = "Static Class";
        else if (type.IsAbstract) kind = "Abstract " + kind;
        if (type.IsSealed && !type.IsEnum && !type.IsValueType) kind = "Sealed " + kind;

        lines.Add($"### {kind}: `{type.Name}`");
        lines.Add("");
        lines.Add($"**Full Name:** `{type.FullName}`");

        if (type.BaseType != null && type.BaseType != typeof(object) && type.BaseType != typeof(ValueType) && type.BaseType != typeof(Enum))
            lines.Add($"**Base Type:** `{GetFriendlyName(type.BaseType)}`");

        var ifaces = type.GetInterfaces()
            .Where(i => i != null)
            .Select(GetFriendlyName)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        if (ifaces.Count > 0)
            lines.Add($"**Interfaces:** {string.Join(", ", ifaces.Select(i => $"`{i}`"))}");

        lines.Add("");

        // Enum values
        if (type.IsEnum)
        {
            lines.Add("#### Values");
            lines.Add("");
            try
            {
                foreach (var name in Enum.GetNames(type))
                {
                    var value = Convert.ChangeType(Enum.Parse(type, name), Enum.GetUnderlyingType(type));
                    lines.Add($"- `{name}` = {value}");
                }
            }
            catch { }
            lines.Add("");
            return lines;
        }

        // Constructors
        var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(c => c.IsPublic)
            .ToList();

        if (ctors.Count > 0)
        {
            lines.Add("#### Constructors");
            lines.Add("");
            foreach (var ctor in ctors.OrderBy(c => c.GetParameters().Length))
                lines.Add($"- `{GetConstructorSignature(ctor)}`");
            lines.Add("");
        }

        // Methods
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.IsPublic && !m.IsSpecialName)
            .OrderBy(m => m.Name)
            .ToList();

        if (methods.Count > 0)
        {
            lines.Add("#### Methods");
            lines.Add("");
            foreach (var m in methods)
                lines.Add($"- `{GetMethodSignature(m)}`");
            lines.Add("");
        }

        // Properties
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(p => p.GetMethod?.IsPublic == true || p.SetMethod?.IsPublic == true)
            .OrderBy(p => p.Name)
            .ToList();

        if (props.Count > 0)
        {
            lines.Add("#### Properties");
            lines.Add("");
            foreach (var p in props)
            {
                var access = "";
                if (p.CanRead && p.GetMethod?.IsPublic == true) access += "get; ";
                if (p.CanWrite && p.SetMethod?.IsPublic == true) access += "set; ";
                var staticMod = (p.GetMethod?.IsStatic == true || p.SetMethod?.IsStatic == true) ? "static " : "";
                lines.Add($"- `{staticMod}{GetFriendlyName(p.PropertyType)} {p.Name} {{ {access.Trim()} }}`");
            }
            lines.Add("");
        }

        // Fields
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f.IsPublic)
            .OrderBy(f => f.Name)
            .ToList();

        if (fields.Count > 0)
        {
            lines.Add("#### Fields");
            lines.Add("");
            foreach (var f in fields)
            {
                var staticMod = f.IsStatic ? "static " : "";
                var constMod = f.IsLiteral ? "const " : "";
                lines.Add($"- `{constMod}{staticMod}{GetFriendlyName(f.FieldType)} {f.Name}`");
            }
            lines.Add("");
        }

        // Events
        var events = type.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(e => e.EventHandlerType != null)
            .OrderBy(e => e.Name)
            .ToList();

        if (events.Count > 0)
        {
            lines.Add("#### Events");
            lines.Add("");
            foreach (var e in events)
                lines.Add($"- `{GetFriendlyName(e.EventHandlerType)} {e.Name}`");
            lines.Add("");
        }

        return lines;
    }

    private static string GetMethodSignature(MethodInfo m)
    {
        var sb = new StringBuilder();
        if (m.IsStatic) sb.Append("static ");
        if (m.IsAbstract) sb.Append("abstract ");
        else if (m.IsVirtual && !m.IsFinal) sb.Append("virtual ");

        sb.Append(GetFriendlyName(m.ReturnType));
        sb.Append(" ");
        sb.Append(m.Name);

        if (m.IsGenericMethod)
        {
            var args = string.Join(", ", m.GetGenericArguments().Select(GetFriendlyName));
            sb.Append($"<{args}>");
        }

        var parameters = m.GetParameters()
            .Select(p =>
            {
                var mod = p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "";
                var typeName = GetFriendlyName(p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType);
                return $"{mod}{typeName} {p.Name}";
            });

        sb.Append("(");
        sb.Append(string.Join(", ", parameters));
        sb.Append(")");

        return sb.ToString();
    }

    private static string GetConstructorSignature(ConstructorInfo c)
    {
        var parameters = c.GetParameters()
            .Select(p =>
            {
                var mod = p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "";
                var typeName = GetFriendlyName(p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType);
                return $"{mod}{typeName} {p.Name}";
            });

        var staticMod = c.IsStatic ? "static " : "";
        return $"{staticMod}{c.DeclaringType?.Name ?? "ctor"}({string.Join(", ", parameters)})";
    }

    private static string GetFriendlyName(Type t)
    {
        if (t == null) return "void";
        if (t == typeof(void)) return "void";

        if (t.IsByRef)
            return GetFriendlyName(t.GetElementType()) + "&";

        if (t.IsArray)
            return GetFriendlyName(t.GetElementType()) + "[]";

        if (t.IsGenericType)
        {
            var name = t.Name;
            var tick = name.IndexOf('`');
            if (tick > 0) name = name.Substring(0, tick);

            var args = string.Join(", ", t.GetGenericArguments().Select(GetFriendlyName));
            return $"{name}<{args}>";
        }

        // Короткие имена для примитивов
        switch (t.FullName)
        {
            case "System.String": return "string";
            case "System.Int32": return "int";
            case "System.Int64": return "long";
            case "System.Boolean": return "bool";
            case "System.Double": return "double";
            case "System.Single": return "float";
            case "System.Decimal": return "decimal";
            case "System.Object": return "object";
            case "System.Void": return "void";
            case "System.Byte": return "byte";
            case "System.Char": return "char";
            case "System.DateTime": return "DateTime";
        }

        return t.Name;
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Assembly";
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}