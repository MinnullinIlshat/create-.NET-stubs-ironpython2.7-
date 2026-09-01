import json
import re
from collections import defaultdict
from pathlib import Path


def generate_python_stubs(json_path: str = "stubs.json", root: str = "."):
    root = Path(root).resolve()
    typings_dir = root / "typings"
    typings_dir.mkdir(parents=True, exist_ok=True)

    with open(json_path, "r", encoding="utf-8") as f:
        lines = json.load(f)

    stubs = parse_python_stubs(lines)

    by_ns = defaultdict(list)
    for full_name, code in stubs.items():
        if not code.strip():
            continue
        parts = full_name.split(".")
        ns = ".".join(parts[:-1]) if len(parts) > 1 else ""
        class_name = parts[-1]
        by_ns[ns].append((class_name, code))

    (typings_dir / "__init__.pyi").write_text("", encoding="utf-8")
    created = 0

    for ns, items in sorted(by_ns.items(), key=lambda x: x[0]):
        folder = typings_dir.joinpath(*ns.split(".")) if ns else typings_dir
        folder.mkdir(parents=True, exist_ok=True)

        current = typings_dir
        if ns:
            for part in ns.split("."):
                current = current / part
                current.mkdir(parents=True, exist_ok=True)
                init = current / "__init__.pyi"
                if not init.exists():
                    init.write_text("", encoding="utf-8")

        names_in_this_file = {name for name, _ in items}
        file_text = merge_namespace_file(ns, items, names_in_this_file)
        (folder / "__init__.pyi").write_text(file_text.rstrip() + "\n", encoding="utf-8")
        created += 1
        print(f"  → {(folder / '__init__.pyi').relative_to(root)}  ({len(items)} classes)")

    print(f"\nГотово. Файлов __init__.pyi с классами: {created}")

_FROM_IMPORT = re.compile(r"^from\s+([A-Za-z0-9_.]+)\s+import\s+(.+)$")
_PLAIN_IMPORT = re.compile(r"^import\s+(.+)$")
_TYPEVAR = re.compile(r"^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*TypeVar\(")
_CLASS = re.compile(r"^class\s+")
_NAMESPACE_COMMENT = re.compile(r"^#\s*namespace:")
_ANY_TOKEN = re.compile(r"\bAny\b")

def normalize_from_import(module, names):
    """
    from System.Collections import Immutable.ImmutableArray
      → module=System.Collections.Immutable, names=[ImmutableArray]
    from A.B.Foo import Foo
      → module=A.B, names=[Foo]
    """
    fixed = []
    for name in names:
        name = name.strip()
        if not name or name == "*":
            continue
        # from X import A.B  →  from X.A import B
        if "." in name:
            prefix, last = name.rsplit(".", 1)
            module = f"{module}.{prefix}"
            name = last
        # from A.B.Foo import Foo  →  from A.B import Foo
        if module.endswith("." + name):
            module = module[: -(len(name) + 1)]
        fixed.append(name)
    return module, fixed

def merge_namespace_file(ns, items, names_in_this_file):
    from_map = defaultdict(set)
    plain_imports = set()
    typevars = {}
    bodies = []
    body_needs_any = False

    for class_name, code in sorted(items, key=lambda x: x[0]):
        imports, tvs, body = split_class_block(code)

        if _ANY_TOKEN.search(body):
            body_needs_any = True

        for raw in imports:
            raw = raw.strip()
            if not raw:
                continue

            m = _FROM_IMPORT.match(raw)
            if m:
                module, names_str = m.group(1), m.group(2)
                star = "*" in names_str
                names = [n.strip() for n in names_str.split(",") if n.strip() and n.strip() != "*"]
                module, names = normalize_from_import(module, names)

                if module == ns:
                    continue
                names = [n for n in names if not (n in names_in_this_file and module == ns)]
                if not names and not star:
                    continue
                if star:
                    from_map[module].add("*")
                else:
                    from_map[module].update(names)
                continue

            m = _PLAIN_IMPORT.match(raw)
            if m:
                plain_imports.add(m.group(1).strip())

        for name, line in tvs.items():
            typevars.setdefault(name, line)

        body = body.strip("\n")
        if body:
            bodies.append(body)

    if body_needs_any:
        from_map["typing"].add("Any")

    out = []

    typing_names = from_map.pop("typing", set())
    abc_names = from_map.pop("collections.abc", set())

    if typing_names:
        out.append("from typing import " + ", ".join(sorted(typing_names)))
    if abc_names:
        out.append("from collections.abc import " + ", ".join(sorted(abc_names)))

    for mod in sorted(plain_imports):
        out.append(f"import {mod}")

    def sort_modules(mod):
        if mod.startswith("System."):
            return (0, mod)
        if mod.startswith("System"):
            return (0, mod)
        return (1, mod)

    for mod in sorted(from_map.keys(), key=sort_modules):
        names = from_map[mod]
        if "*" in names:
            out.append(f"from {mod} import *")
        else:
            out.append(f"from {mod} import {', '.join(sorted(names))}")

    if out:
        out.append("")

    for name in sorted(typevars):
        out.append(typevars[name])
    if typevars:
        out.append("")

    out.append("\n\n".join(bodies))
    out.append("")
    return "\n".join(out)

def split_class_block(code):
    imports = []
    typevars = {}
    body_lines = []
    seen_class = False

    for line in code.splitlines():
        stripped = line.strip()
        if not seen_class:
            if _CLASS.match(stripped):
                seen_class = True
                body_lines.append(line)
                continue
            if _NAMESPACE_COMMENT.match(stripped):
                continue
            tv = _TYPEVAR.match(stripped)
            if tv:
                typevars[tv.group(1)] = stripped
                continue
            if stripped.startswith(("from ", "import ")):
                imports.append(stripped)
                continue
            continue
        body_lines.append(line)

    return imports, typevars, "\n".join(body_lines)

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

    def flush():
        nonlocal in_class, current_code, current_class_name, current_namespace
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

    for line in lines:
        stripped = line.strip()

        if stripped.startswith("# === END OF TYPE"):
            flush()
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
            if (stripped.startswith(("from ", "import ")) or stripped == "" or "TypeVar(" in stripped):
                current_code.append(line)
            continue

        current_code.append(line)

    flush()
    return stubs

if __name__ == "__main__":
    generate_python_stubs(json_path="stubs.json", root=".")
