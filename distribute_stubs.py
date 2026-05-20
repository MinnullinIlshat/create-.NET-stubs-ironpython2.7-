# -*- coding: utf-8 -*-
import os
from collections import defaultdict, OrderedDict
from pathlib import Path
from typing import List, Tuple


def parse_big_pyi(file_path: str) -> List[Tuple[str, List[str]]]:
    """Разбирает большой .pyi файл на блоки по классам.
    Каждый блок начинается со строки '# Full.Path.ClassName'."""
    with open(file_path, "r", encoding="utf-8") as f:
        lines = f.readlines()

    blocks: List[Tuple[str, List[str]]] = []
    current_fullname = None
    current_block: List[str] = []

    for line in lines:
        stripped = line.strip()

        # Начало нового класса
        if (
            stripped.startswith("# ")
            and "." in stripped
            and "import" not in stripped.lower()
            and "from " not in stripped.lower()
        ):
            # Сохраняем предыдущий блок
            if current_block and current_fullname:
                blocks.append((current_fullname, current_block))

            current_fullname = stripped[2:].strip()
            current_block = [line]
            continue

        # Продолжаем текущий блок (включая импорты следующего класса — они будут обработаны позже)
        if current_block:
            current_block.append(line)

    # Последний блок
    if current_block and current_fullname:
        blocks.append((current_fullname, current_block))

    return blocks


def get_namespace(fullname: str) -> str:
    """Из 'Namespace.Idm.Integration.Unit' возвращает 'Namespace.Idm.Integration'"""
    if not fullname or "." not in fullname:
        return ""
    return ".".join(fullname.split(".")[:-1])


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


def distribute_stubs(big_pyi_path_str: str, output_root_str: str = ".") -> None:
    """
    Основная функция.
    Читает большой .pyi, убирает дубли импортов внутри каждого namespace
    и распределяет код по папкам с __init__.pyi.
    """
    big_pyi_path = Path(big_pyi_path_str)
    if not big_pyi_path.is_file():
        print(f"Ошибка: файл '{big_pyi_path}' не найден.")
        return

    blocks = parse_big_pyi(str(big_pyi_path))

    # Группировка по namespace
    # namespace → {imports: OrderedDict (для сохранения порядка), classes: list}
    namespace_data = defaultdict(lambda: {"imports": OrderedDict(), "classes": []})

    for fullname, block_lines in blocks:
        ns = get_namespace(fullname)
        imports, body = extract_imports_and_body(block_lines)

        # Добавляем импорты (дубли автоматически удаляются)
        for imp in imports:
            namespace_data[ns]["imports"][imp] = None

        # Сохраняем тело класса
        namespace_data[ns]["classes"].append((fullname, body))

    output_root = Path(output_root_str)
    output_root.mkdir(parents=True, exist_ok=True)

    for ns, data in namespace_data.items():
        if not ns:
            continue

        # Папка для namespace: Namespace/Idm/Integration
        folder = output_root / Path(ns.replace(".", os.sep))
        folder.mkdir(parents=True, exist_ok=True)

        init_file = folder / "__init__.pyi"

        with open(init_file, "w", encoding="utf-8") as f:
            # 1. from __future__ import annotations — всегда в самом верху
            f.write("from __future__ import annotations\n\n")

            # 2. Все уникальные импорты (в порядке первого появления)
            for imp_line in data["imports"].keys():
                f.write(imp_line + "\n")

            if data["imports"]:
                f.write("\n")

            # 3. Все классы этого namespace
            for fullname, body_lines in data["classes"]:
                for line in body_lines:
                    f.write(line)
                f.write("\n")  # разделитель между классами

        print(f"✓ Записано: {init_file}  ({len(data['classes'])} классов)")

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
    print("Дублирование импортов полностью устранено.")
    print(
        "Теперь каждый __init__.pyi содержит чистый, единственный блок импортов вверху."
    )
    print("\nПример импорта:")
    print("    from Namespace.Idm.Integration import Unit")


# ====================== ИСПОЛЬЗОВАНИЕ ======================
if __name__ == "__main__":
    # ←←← ИЗМЕНИТЕ ЭТИ ДВЕ СТРОКИ НА СВОИ ←←←
    BIG_PYI_FILE = "all_stubs.pyi"  # путь к вашему большому файлу со стабами
    OUTPUT_ROOT = "."  # куда создавать структуру (можно "stubs")

    distribute_stubs(BIG_PYI_FILE, OUTPUT_ROOT)
