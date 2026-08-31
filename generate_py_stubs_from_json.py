import json
import re
from pathlib import Path

def generate_python_stubs(json_path: str = "stubs.json", root: str = "."):
    root = Path(root).resolve()
    typings_dir = root / "typings"
    typings_dir.mkdir(parents=True, exist_ok=True)

    with open(json_path, "r", encoding="utf-8") as f:
        lines = json.load(f)

    stubs = parse_python_stubs(lines)

    created = 0
    created_inits = 0

    root_init = typings_dir / "__init__.pyi"
    if not root_init.exists():
        root_init.write_text("", encoding="utf-8")
        created_inits += 1

    for full_name, code in stubs.items():
        if not code.strip():
            continue

        parts = full_name.split(".")
        if len(parts) > 1:
            folder = typings_dir.joinpath(*parts[:-1])
            filename = parts[-1] + ".pyi"
        else:
            folder = typings_dir
            filename = full_name + ".pyi"

        folder.mkdir(parents=True, exist_ok=True)

        current = typings_dir
        for part in parts[:-1]:
            current = current / part
            init_file = current / "__init__.pyi"
            if not init_file.exists():
                init_file.write_text("", encoding="utf-8")
                created_inits += 1

        (folder / filename).write_text(code.rstrip() + "\n", encoding="utf-8")
        created += 1
        print(f"  → {(folder / filename).relative_to(root)}")

    print(f"\nГотово. Стабов: {created}, __init__.pyi: {created_inits}")

def parse_python_stubs(lines):
    stubs = {}
    current_namespace = ""
    current_class_name = ""
    current_code = []
    in_class = False

    namespace_pattern = re.compile(r"^#\s*namespace:\s*([A-Za-z0-9_.]+)\s*$")
    class_pattern = re.compile(
        r"^class\s+([A-Za-z_][A-Za-z0-9_]*)"
        r"(?:\s*\[[^\]]*\])?"
        r"(?:\s*\([^)]*\))?"
        r"\s*:"
    )
    using_or_import = re.compile(r"^(from |import |[A-Za-z_][A-Za-z0-9_]* = TypeVar)")

    for line in lines:
        stripped = line.strip()

        if stripped.startswith("# === END OF TYPE"):
            if in_class and current_class_name:
                full_name = (
                    f"{current_namespace}.{current_class_name}"
                    if current_namespace else current_class_name
                )
                stubs[full_name] = "\n".join(current_code).rstrip()
            in_class = False
            current_code = []
            current_class_name = ""
            current_namespace = ""
            continue

        ns_match = namespace_pattern.match(stripped)
        if ns_match and not in_class:
            current_namespace = ns_match.group(1)
            current_code.append(line)
            continue

        class_match = class_pattern.match(stripped)
        if class_match and not in_class:
            current_class_name = class_match.group(1)
            current_code.append(line)
            in_class = True
            continue

        if not in_class:
            if (stripped.startswith("from ") or stripped.startswith("import ") or
                stripped == "" or "TypeVar(" in stripped):
                current_code.append(line)
            continue

        current_code.append(line)

    if in_class and current_class_name:
        full_name = (
            f"{current_namespace}.{current_class_name}"
            if current_namespace else current_class_name
        )
        stubs[full_name] = "\n".join(current_code).rstrip()

    return stubs

if __name__ == "__main__":
    generate_python_stubs(json_path="stubs.json", root=".")
