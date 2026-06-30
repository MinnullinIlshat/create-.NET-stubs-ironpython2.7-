import json
import os
import re
from pathlib import Path

def generate_stubs_from_json(json_path: str, output_dir: str = "Stubs"):
    """
    Читает JSON с массивом строк (результат C# StubGenerator.GenerateStubs())
    и раскладывает код по папкам namespace и файлам .cs
    """
    with open(json_path, 'r', encoding='utf-8') as f:
        lines = json.load(f)

    output_path = Path(output_dir)
    output_path.mkdir(exist_ok=True)

    current_namespace = ""
    current_type_name = ""
    current_code = []
    files_created = 0

    # Регулярка для поиска объявления типа
    type_pattern = re.compile(
        r'^\s*public\s+(?:(abstract|sealed|static)\s+)?(class|interface|enum|struct|record)\s+([A-Za-z_][A-Za-z0-9_]*)'
    )

    namespace_pattern = re.compile(r'^\s*namespace\s+([A-Za-z0-9_.]+)\s*;')

    for line in lines:
        stripped = line.strip()

        # Ищем namespace
        ns_match = namespace_pattern.match(stripped)
        if ns_match:
            current_namespace = ns_match.group(1)
            continue

        # Ищем объявление нового типа
        type_match = type_pattern.match(stripped)
        if type_match:
            # Если уже собирали предыдущий тип — сохраняем его
            if current_type_name and current_code:
                save_type_to_file(output_path, current_namespace, current_type_name, current_code)
                files_created += 1

            # Начинаем новый тип
            current_type_name = type_match.group(3)
            current_code = [line]
            continue

        # Если мы внутри типа — добавляем строку
        if current_type_name:
            current_code.append(line)

            # Конец типа (простая эвристика)
            if stripped == "}":
                # Сохраняем тип
                save_type_to_file(output_path, current_namespace, current_type_name, current_code)
                files_created += 1
                current_type_name = ""
                current_code = []

    # Сохраняем последний тип, если остался
    if current_type_name and current_code:
        save_type_to_file(output_path, current_namespace, current_type_name, current_code)
        files_created += 1

    print(f"✅ Готово! Создано файлов: {files_created}")
    print(f"📁 Стабы сохранены в папку: {output_path.absolute()}")


def save_type_to_file(base_path: Path, namespace: str, type_name: str, code_lines: list):
    if not namespace or not type_name:
        return

    # Преобразуем namespace в путь (YourCompany.Core → YourCompany/Core)
    namespace_path = namespace.replace('.', os.sep)
    folder = base_path / namespace_path
    folder.mkdir(parents=True, exist_ok=True)

    file_path = folder / f"{type_name}.cs"

    with open(file_path, 'w', encoding='utf-8') as f:
        f.write('\n'.join(code_lines))

    print(f"  → {file_path}")


if __name__ == "__main__":
    # === НАСТРОЙКИ ===
    JSON_FILE = "stubs.json"          # Имя JSON-файла с массивом строк
    OUTPUT_FOLDER = "Stubs"           # Куда сохранять стабы

    generate_stubs_from_json(JSON_FILE, OUTPUT_FOLDER)
