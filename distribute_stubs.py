# -*- coding: utf-8 -*-
import os
from collections import defaultdict, OrderedDict
from pathlib import Path
from typing import List, Tuple, Set


def parse_big_pyi(file_path: str) -> List[Tuple[str, List[str]]]:
    """Разбирает большой .pyi файл на блоки по классам."""
    with open(file_path, "r", encoding="utf-8") as f:
        lines = f.readlines()

    blocks: List[Tuple[str, List[str]]] = []
    current_fullname = None
    current_block: List[str] = []

    for line in lines:
        stripped = line.strip()

        if (
            stripped.startswith("# ")
            and "." in stripped
            and "import" not in stripped.lower()
            and "from " not in stripped.lower()
        ):
            if current_block and current_fullname:
                blocks.append((current_fullname, current_block))

            current_fullname = stripped[2:].strip()
            current_block = [line]
            continue

        if current_block:
            current_block.append(line)

    if current_block and current_fullname:
        blocks.append((current_fullname, current_block))

    return blocks


def get_namespace(fullname: str) -> str:
    """Возвращает namespace без имени класса."""
    if not fullname or "." not in fullname:
        return ""
    return ".".join(fullname.split(".")[:-1])


def get_class_name(fullname: str) -> str:
    """Возвращает только имя класса из полного пути."""
    return fullname.split(".")[-1] if "." in fullname else fullname


def extract_imports_and_body(block_lines: List[str]) -> Tuple[List[str], List[str]]:
    """Разделяет блок на импорты и тело класса."""
    imports: List[str] = []
    body: List[str] = []

    for line in block_lines:
        stripped = line.strip()
        if stripped.startswith(("from ", "import ")) or stripped.startswith(
            "from __future__"
        ):
            imports.append(line.rstrip())
        else:
            body.append(line)

    return imports, body


def is_self_import(
    import_line: str, current_namespace: str, local_classes: Set[str]
) -> bool:
    """
    Проверяет, является ли импорт самоимпортом.
    Примеры, которые нужно убрать:
        from Namespace.Idm.Integration import Unit
        from . import Unit
    """
    import_line = import_line.strip()

    # Случай "from . import ..."
    if import_line.startswith("from . import "):
        imported_names = [
            n.strip() for n in import_line.split("import", 1)[1].split(",")
        ]
        return any(name in local_classes for name in imported_names)

    # Случай "from Полный.Namespace import ..."
    if import_line.startswith("from ") and " import " in import_line:
        parts = import_line.split(" import ", 1)
        from_part = parts[0].replace("from ", "").strip()
        imported_names = [n.strip() for n in parts[1].split(",")]

        # Если from_part совпадает с текущим namespace — это самоимпорт
        if from_part == current_namespace:
            return any(name in local_classes for name in imported_names)

    return False


def distribute_stubs(big_pyi_path_str: str, output_root_str: str = ".") -> None:
    """
    Основная функция с исправлением самоимпортов.
    """
    big_pyi_path = Path(big_pyi_path_str)
    if not big_pyi_path.is_file():
        print(f"Ошибка: файл '{big_pyi_path}' не найден.")
        return

    blocks = parse_big_pyi(str(big_pyi_path))

    # Группировка по namespace
    namespace_data = defaultdict(
        lambda: {"imports": OrderedDict(), "classes": [], "local_classes": set()}
    )

    for fullname, block_lines in blocks:
        ns = get_namespace(fullname)
        class_name = get_class_name(fullname)

        imports, body = extract_imports_and_body(block_lines)

        namespace_data[ns]["local_classes"].add(class_name)
        for imp in imports:
            namespace_data[ns]["imports"][imp] = None
        namespace_data[ns]["classes"].append((fullname, body))

    output_root = Path(output_root_str)
    output_root.mkdir(parents=True, exist_ok=True)

    for ns, data in namespace_data.items():
        if not ns:
            continue

        folder = output_root / Path(ns.replace(".", os.sep))
        folder.mkdir(parents=True, exist_ok=True)
        init_file = folder / "__init__.pyi"

        local_classes = data["local_classes"]

        with open(init_file, "w", encoding="utf-8") as f:
            # 1. from __future__
            f.write("from __future__ import annotations\n\n")

            # 2. Импорты (исключаем самоимпорты)
            written_imports = 0
            for imp_line in data["imports"].keys():
                if not is_self_import(imp_line, ns, local_classes):
                    f.write(imp_line + "\n")
                    written_imports += 1

            if written_imports > 0:
                f.write("\n")

            # 3. Тела всех классов этого namespace
            for fullname, body_lines in data["classes"]:
                for line in body_lines:
                    f.write(line)
                f.write("\n")

        print(
            f"✓ Записано: {init_file}  ({len(data['classes'])} классов, импортов после очистки: {written_imports})"
        )

    # Создаём пустые __init__.pyi для всех промежуточных пакетов
    created = set()
    for ns in namespace_data.keys():
        if not ns:
            continue
        parts = ns.split(".")
        current = output_root
        for i in range(len(parts)):
            pkg_path = current / "/".join(parts[: i + 1])
            init_file = pkg_path / "__init__.pyi"
            if init_file not in created:
                pkg_path.mkdir(parents=True, exist_ok=True)
                if not init_file.exists():
                    init_file.write_text(
                        "# Package stub (для корректных импортов)\n", encoding="utf-8"
                    )
                created.add(init_file)

    print("\nГотово!")


# ====================== ИСПОЛЬЗОВАНИЕ ======================
if __name__ == "__main__":
    # ←←← ИЗМЕНИТЕ ЭТИ ДВЕ СТРОКИ НА СВОИ ←←←
    BIG_PYI_FILE = "all_stubs.pyi"  # путь к вашему большому файлу
    OUTPUT_ROOT = "."  # куда создавать структуру

    distribute_stubs(BIG_PYI_FILE, OUTPUT_ROOT)
