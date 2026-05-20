#!/usr/bin/env python3
"""
Скрипт удаляет всю статическую типизацию из .py файла.
Перезаписывает оригинальный файл.
"""

import ast
import sys
from pathlib import Path


class TypeHintRemover(ast.NodeTransformer):

    def visit_FunctionDef(self, node):
        node.returns = None
        for arg in (node.args.args + node.args.posonlyargs + 
                   node.args.kwonlyargs):
            arg.annotation = None
        if node.args.vararg:
            node.args.vararg.annotation = None
        if node.args.kwarg:
            node.args.kwarg.annotation = None
        self.generic_visit(node)
        return node

    def visit_AsyncFunctionDef(self, node):
        return self.visit_FunctionDef(node)

    def visit_AnnAssign(self, node):
        # x: int = 5 → x = 5
        # x: int     → удаляем строку полностью
        if node.value is None:
            return None
        return ast.Assign(targets=[node.target], value=node.value)

    def visit_Import(self, node):
        # Убираем import typing
        node.names = [n for n in node.names if n.name != "typing"]
        return node if node.names else None

    def visit_ImportFrom(self, node):
        # Убираем from typing import ...
        if node.module in ("typing", "typing_extensions"):
            return None
        return node


def strip_type_hints(code: str) -> str:
    tree = ast.parse(code)
    transformed = TypeHintRemover().visit(tree)
    ast.fix_missing_locations(transformed)
    return ast.unparse(transformed) + "\n"


def main():
    if len(sys.argv) < 2:
        print("Использование: python strip.py <файл.py>")
        sys.exit(1)

    filepath = Path(sys.argv[1])

    if not filepath.exists():
        print(f"Ошибка: файл не найден — {filepath}")
        sys.exit(1)

    # Читаем файл
    with open(filepath, "r", encoding="utf-8") as f:
        original_code = f.read()

    # Убираем type hints
    cleaned_code = strip_type_hints(original_code)

    # Перезаписываем оригинал
    with open(filepath, "w", encoding="utf-8") as f:
        f.write(cleaned_code)

    print("✓ Типизация успешно удалена")
    print(f"   Файл: {filepath}")


if __name__ == "__main__":
    main()
