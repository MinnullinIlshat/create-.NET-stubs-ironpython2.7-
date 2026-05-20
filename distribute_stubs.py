# -*- coding: utf-8 -*-
"""
IronPython 2.7 — Скрипт для распределения одного большого .pyi файла
по правильной иерархии папок с __init__.pyi.

Пример:
    # Namespace.Idm.Integration.Unit
    class Unit...
    → создаст папку Namespace/Idm/Integration/__init__.pyi

Импорт будет работать:
    from Namespace.Idm.Integration import Unit
"""

import os
from collections import defaultdict


def get_namespace(full_path):
    full_path = full_path.strip()
    if not full_path or "." not in full_path:
        return ""
    # Убираем имя самого класса
    parts = full_path.split(".")
    return ".".join(parts[:-1])


def ensure_package_init(dir_path):
    """Создаёт папку и пустой __init__.pyi (если его ещё нет)"""
    os.makedirs(dir_path, exist_ok=True)
    init_path = os.path.join(dir_path, "__init__.pyi")
    if not os.path.exists(init_path):
        with open(init_path, "w", encoding="utf-8") as f:
            f.write("# Empty package stub — нужен для корректного импорта\n")
        print("   └── создан пустой __init__.pyi в", dir_path)


def distribute_stubs(big_pyi_path, output_root="."):
    """
    Основная функция.

    big_pyi_path — путь к большому файлу со всеми стабами
    output_root   — корневая папка, куда будет создана структура
    """
    if not os.path.isfile(big_pyi_path):
        print("Ошибка: файл '{}' не найден.".format(big_pyi_path))
        return

    with open(big_pyi_path, "r", encoding="utf-8") as f:
        lines = f.readlines()

    # namespace → список строк кода (включая # комментарий и весь класс)
    namespace_blocks = defaultdict(list)

    current_block = []
    current_full_path = None

    for line in lines:
        stripped = line.strip()

        # === НАЧАЛО НОВОГО КЛАССА ===
        if (
            stripped.startswith("# ")
            and "." in stripped
            and not stripped.startswith(("# from ", "# import "))
        ):
            # Сохраняем предыдущий блок
            if current_block and current_full_path is not None:
                ns = get_namespace(current_full_path)
                namespace_blocks[ns].extend(current_block)

            # Начинаем новый блок
            current_full_path = stripped[2:].strip()  # убираем "# "
            current_block = [line]
            continue

        # Продолжаем текущий блок
        if current_block:
            current_block.append(line)

    # Последний блок
    if current_block and current_full_path is not None:
        ns = get_namespace(current_full_path)
        namespace_blocks[ns].extend(current_block)

    # === ЗАПИСЬ В ФАЙЛЫ ===
    for namespace, block in namespace_blocks.items():
        if not namespace:
            continue

        # Путь к папке: Namespace/Idm/Integration
        folder = os.path.join(output_root, *namespace.split("."))

        # Создаём все родительские __init__.pyi
        parts = namespace.split(".")
        current = output_root
        for i in range(len(parts)):
            current = os.path.join(current, parts[i])
            ensure_package_init(current)

        # Пишем сам __init__.pyi с кодом класса(ов)
        init_file = os.path.join(folder, "__init__.pyi")

        with open(init_file, "w", encoding="utf-8") as f:
            content = "".join(block).strip() + "\n"
            f.write(content)

        print("✓ Записано: {}".format(init_file))

    # Корневой __init__.pyi
    root_init = os.path.join(output_root, "__init__.pyi")
    if not os.path.exists(root_init):
        ensure_package_init(output_root)
        print("✓ Создан корневой __init__.pyi")

    print("\nГотово! Структура папок успешно создана.")


# ====================== ИСПОЛЬЗОВАНИЕ ======================
if __name__ == "__main__":
    # ←←← ИЗМЕНИТЕ ЭТИ ДВЕ СТРОКИ НА СВОИ ←←←
    BIG_PYI_FILE = "all_stubs.pyi"  # путь к вашему большому файлу
    OUTPUT_ROOT = "."  # куда класть структуру (можно "stubs" или "Namespace")

    distribute_stubs(BIG_PYI_FILE, OUTPUT_ROOT)
