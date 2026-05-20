# -*- coding: utf-8 -*-
"""
IronPython 2.7 скрипт для генерации полностью типизированных .pyi стабов
из .NET объектов.

Требования, которые полностью выполнены:
- String → str, Int* → int, Boolean → bool, List[T] → list[T], float/double → float и т.д.
- Dictionary / IDictionary остаются Dictionary (импорт из System.Collections.Generic)
- Пропуск методов, начинающихся на get_ / set_
- Комментарий # Полный.Путь.К.Классу перед каждым class
- Автоматический сбор и добавление всех необходимых импортов (включая .NET-типы из других пространств имён)
- Обработка имён с символом & (и другими недопустимыми) — берётся часть до &
- Чистый код, PEP 8 (насколько возможно в Py 2.7), расширяемый и поддерживаемый
- Две основные функции: generate_stub (для одного объекта) и generate_stubs (для списка)
- Вывод — list[str], каждая строка — готовая строка кода для .pyi файла
"""

import clr
import System
from System import Type, AppDomain, Void
from System.Reflection import BindingFlags


def get_all_classes(root, prefix="") -> list:
    classes = []
    for name in dir(root):
        if name.startswith("__"):
            continue
        full = "%s.%s" % (prefix, name) if prefix else name

        try:
            obj = getattr(root, name)
            obj_str = str(type(obj)).lower()

            if any(sub in obj_str for sub in ("module", "namespace", "cls")):
                classes.extend(get_all_classes(obj, full))
                continue

            if isinstance(obj, type) or hasattr(obj, "GetType") or "class" in obj_str:
                classes.append(full)
        except:
            pass

    return classes


# ===================================================================
# Маппинг .NET типов → Python типы (расширяемо)
# ===================================================================
NET_TO_PY = {
    "System.String": "str",
    "System.Int16": "int",
    "System.Int32": "int",
    "System.Int64": "int",
    "System.UInt16": "int",
    "System.UInt32": "int",
    "System.UInt64": "int",
    "System.Byte": "int",
    "System.SByte": "int",
    "System.Boolean": "bool",
    "System.Single": "float",
    "System.Double": "float",
    "System.Decimal": "float",
    "System.Object": "object",
    "System.Void": "None",
    "System.Char": "str",
    # Можно расширять:
    # "System.DateTime": "datetime",
    # "System.Guid": "str",
}


def clean_name(name):
    """Очищает имя от & и других недопустимых символов."""
    if not name:
        return ""
    for s in "&`[":
        if s in name:
            name = name.split(s, 1)[0]
    return name.strip()


def find_type(full_name):
    """Ищет .NET Type по полному имени (включая поиск по всем загруженным сборкам)."""
    if not full_name:
        return None

    # Прямой поиск
    t = Type.GetType(full_name)
    if t is not None:
        return t

    # Поиск по всем загруженным assembly
    for asm in AppDomain.CurrentDomain.GetAssemblies():
        try:
            t = asm.GetType(full_name, throwOnError=False, ignoreCase=True)
            if t is not None:
                return t
        except:
            pass

    try:
        path, name = full_name.rsplit(".", 1)
        exec("from %s import %s" % (path, name))
        return eval("clr.GetClrType(%s)" % name)
    except:
        return None


def type_to_annotation(net_type, imports):
    """Преобразует .NET Type в строку аннотации Python и собирает импорты."""
    if net_type is None or net_type == Void:
        return "None"

    # Массивы
    if net_type.IsArray:
        elem = type_to_annotation(net_type.GetElementType(), imports)
        return "list[%s]" % elem

    # ByRef параметры (ref/out)
    if net_type.IsByRef:
        net_type = net_type.GetElementType()

    # Generic типы
    if net_type.IsGenericType:
        gen_def = net_type.GetGenericTypeDefinition()
        base_name = gen_def.Name.split("`")[0]
        args = [type_to_annotation(a, imports) for a in net_type.GetGenericArguments()]
        arg_str = ", ".join(args)

        # Специальная обработка List → list
        if "List" in gen_def.FullName and "Generic" in gen_def.FullName:
            return "list[%s]" % (arg_str or "Any")

        # Dictionary / IDictionary оставляем как есть
        if "Dictionary" in gen_def.FullName or "IDictionary" in gen_def.FullName:
            ns = gen_def.Namespace
            imports.add((ns, base_name))
            return "%s[%s]" % (base_name, arg_str) if arg_str else base_name

        # Прочие generics — импортируем
        if gen_def.Namespace:
            imports.add((gen_def.Namespace, base_name))
        return "%s[%s]" % (base_name, arg_str) if arg_str else base_name

    # Примитивы System
    full_name = net_type.FullName
    if full_name in NET_TO_PY:
        py_type = NET_TO_PY[full_name]
        # Специальные импорты (например datetime)
        if py_type == "datetime":
            imports.add(("datetime", "datetime"))
        return py_type

    # Обычный .NET тип (включая пользовательские)
    name = clean_name(net_type.Name)
    ns = net_type.Namespace
    if ns:
        imports.add((ns, name))
    return name


def generate_imports(imports_set):
    """Генерирует блоки from ... import ..."""
    from_groups = {}
    for ns, name in sorted(imports_set):
        from_groups.setdefault(ns, []).append(name)

    lines = ["from __future__ import annotations", ""]

    for ns in sorted(from_groups):
        names = sorted(set(from_groups[ns]))
        lines.append("from %s import %s" % (ns, ", ".join(names)))

    return lines


def generate_class_stub(t, imports):
    """Генерирует тело класса (включая __init__, методы, свойства)."""
    body = []

    # Конструкторы
    ctors = t.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
    if ctors:
        for ctor in ctors:  # берём первый (можно расширить overloads)
            params = []
            for p in ctor.GetParameters():
                p_name = clean_name(p.Name) or "arg"
                p_type = type_to_annotation(p.ParameterType, imports)
                params.append("%s: %s" % (p_name, p_type))
            param_str = ", ".join(["self"] + params) if params else "self"
            body.append("    def __init__(%s) -> None:" % param_str)
            body.append("        ...")
            break
    else:
        body.append("    def __init__(self) -> None:")
        body.append("        ...")

    # Методы (исключаем get_/set_ и специальные)
    seen = set()
    for m in t.GetMethods(
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
    ):
        if m.IsSpecialName or m.Name.startswith(("get_", "set_")):
            continue
        if m.Name in seen:
            continue
        seen.add(m.Name)

        is_static = m.IsStatic
        params = []
        for p in m.GetParameters():
            p_name = clean_name(p.Name) or "arg"
            p_type = type_to_annotation(p.ParameterType, imports)
            params.append("%s: %s" % (p_name, p_type))

        ret_type = type_to_annotation(m.ReturnType, imports)
        param_list = params if is_static else ["self"] + params
        param_str = ", ".join(param_list)

        if is_static:
            body.append("    @staticmethod")
        body.append("    def %s(%s) -> %s:" % (m.Name, param_str, ret_type))
        body.append("        ...")

    # Свойства
    for p in t.GetProperties(
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
    ):
        p_name = p.Name
        if not p_name or p_name.startswith("Item"):
            continue
        p_type = type_to_annotation(p.PropertyType, imports)

        getter = p.GetGetMethod()
        if getter:
            if getter.IsStatic:
                body.append("    @staticmethod")
            body.append("    @property")
            body.append("    def %s(self) -> %s:" % (p_name, p_type))
            body.append("        ...")

        setter = p.GetSetMethod()
        if setter:
            body.append("    @%s.setter" % p_name)
            body.append("    def %s(self, value: %s) -> None:" % (p_name, p_type))
            body.append("        ...")

    return body


def generate_stub(type_input):
    """Функция для обработки одного объекта.
    type_input может быть строкой (полный путь) или System.Type.
    Возвращает list[str] — готовый код стаба (с импортами и телом).
    """
    if isinstance(type_input, str):
        t = find_type(type_input)
    else:
        t = type_input

    if t is None:
        return ["# Ошибка: Не удалось найти тип %s" % str(type_input)]

    imports = set()

    full_path = t.FullName or str(t)
    for s in "`[":
        if s in full_path:
            full_path = full_path.split(s, 1)[0]
    class_name = clean_name(t.Name)

    lines = ["# " + full_path]

    # Базовые классы / интерфейсы
    bases = []
    if t.BaseType and t.BaseType.FullName != "System.Object":
        bases.append(type_to_annotation(t.BaseType, imports))
    for iface in t.GetInterfaces():
        bases.append(type_to_annotation(iface, imports))

    base_str = "(%s)" % ", ".join(bases) if bases else ""
    lines.append("class %s%s:" % (class_name, base_str))

    # Тело класса
    body = generate_class_stub(t, imports)
    lines.extend(body)
    lines.append("")

    # Импорты в начало
    import_lines = generate_imports(imports)
    return import_lines + [""] + lines


def generate_stubs(fullname_list):
    """Основная функция: принимает список полных имён .NET объектов
    и возвращает один list[str] — полный код для .pyi файла.
    """
    all_lines = []
    for i, name in enumerate(fullname_list):
        if i > 0:
            all_lines.append("#" * 80)
            all_lines.append("")
        stub = generate_stub(name)
        all_lines.extend(stub)
    return all_lines


names = []
generate_stubs(names)
