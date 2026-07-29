import json
import re
from pathlib import Path

def generate_python_stubs(json_path: str = "stubs.json", root: str = "."):
    root = Path(root).resolve()
    typings_dir = root / "typings"
    typings_dir.mkdir(parents=True, exist_ok=True)

    print(f"📁 Рабочая директория: {root}")
    print(f"📁 Папка стабов: {typings_dir}")

    with open(json_path, "r", encoding="utf-8") as f:
        lines = json.load(f)

    stubs = parse_python_stubs(lines)

    created = 0
    created_inits = 0

    for full_name, code in stubs.items():
        if not code.strip():
            continue

        parts = full_name.split(".")
        if len(parts) > 1:
            folder = typings_dir / Path(*parts[:-1])
            filename = parts[-1] + ".pyi"
        else:
            folder = typings_dir
            filename = full_name + ".pyi"

        # Создаём все родительские папки
        folder.mkdir(parents=True, exist_ok=True)

        # === Создаём __init__.pyi во всех папках по пути ===
        current = typings_dir
        # parts[:-1] — это namespace-части
        for part in parts[:-1]:
            current = current / part
            init_file = current / "__init__.pyi"
            if not init_file.exists():
                init_file.write_text("", encoding="utf-8")  # пустой файл
                created_inits += 1

        # На всякий случай создаём __init__.pyi и в корне typings
        root_init = typings_dir / "__init__.pyi"
        if not root_init.exists():
            root_init.write_text("", encoding="utf-8")
            created_inits += 1

        # Записываем сам стаб
        file_path = folder / filename
        file_path.write_text(code.rstrip() + "\n", encoding="utf-8")
        created += 1
        print(f"  → {file_path.relative_to(root)}")

    print(f"\n✅ Готово!")
    print(f"   Создано файлов стабов: {created}")
    print(f"   Создано __init__.pyi:  {created_inits}")
    print(f"Стабы лежат в: {typings_dir}")


def parse_python_stubs(lines: list[str]) -> dict[str, str]:
    """
    Парсит вывод C# PythonStubGenerator.
    Ищет блоки вида:
        from typing import ...
        from System import ...
        # namespace: Some.Namespace
        class ClassName:
            ...
        # === END OF TYPE ===
    """
    stubs: dict[str, str] = {}

    current_namespace = ""
    current_class_name = ""
    current_code: list[str] = []
    in_class = False

    namespace_pattern = re.compile(r"^#\s*namespace:\s*([A-Za-z0-9_.]+)\s*$")
    class_pattern = re.compile(r"^class\s+([A-Za-z_][A-Za-z0-9_]*)\s*[:\(]")
    end_marker = "# === END OF TYPE ==="

    for line in lines:
        stripped = line.strip()

        # Конец текущего типа
        if stripped == end_marker or stripped.startswith("# === END OF TYPE"):
            if in_class and current_class_name:
                full_name = f"{current_namespace}.{current_class_name}" if current_namespace else current_class_name
                stubs[full_name] = "\n".join(current_code).rstrip()
            in_class = False
            current_code = []
            current_class_name = ""
            continue

        # Namespace
        ns_match = namespace_pattern.match(stripped)
        if ns_match and not in_class:
            current_namespace = ns_match.group(1)
            # namespace-комментарий тоже сохраняем в код (полезно)
            current_code.append(line)
            continue

        # Начало класса
        class_match = class_pattern.match(stripped)
        if class_match and not in_class:
            current_class_name = class_match.group(1)
            current_code.append(line)
            in_class = True
            continue

        # Всё, что идёт до класса (import'ы) и внутри класса
        if not in_class:
            # Собираем import'ы и пустые строки до класса
            if stripped.startswith("from ") or stripped.startswith("import ") or stripped == "":
                current_code.append(line)
            continue

        # Уже внутри класса
        if in_class:
            current_code.append(line)

    # На случай, если последний блок не закрыт маркером
    if in_class and current_class_name:
        full_name = f"{current_namespace}.{current_class_name}" if current_namespace else current_class_name
        stubs[full_name] = "\n".join(current_code).rstrip()

    return stubs


if __name__ == "__main__":
    # === НАСТРОЙКИ ===
    JSON_FILE = "stubs.json"          # файл с результатом PythonStubGenerator
    OUTPUT_ROOT = "."                 # корень проекта

    generate_python_stubs(json_path=JSON_FILE, root=OUTPUT_ROOT)
