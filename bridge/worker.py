"""Puente JSON hacia las skills UNO de Writer y Calc.

El proceso lo lanza C# con el python.exe de LibreOffice. Lee un objeto JSON
por stdin y escribe un único objeto JSON por stdout. No ejecuta código recibido
en la petición: solo operaciones del catálogo cerrado.
"""

from __future__ import annotations

import json
import re
import sys
import traceback
from pathlib import Path
from typing import Any


HEADING_STYLES = {
    1: ("Heading 1", "Encabezado 1"),
    2: ("Heading 2", "Encabezado 2"),
    3: ("Heading 3", "Encabezado 3"),
    4: ("Heading 4", "Encabezado 4"),
    5: ("Heading 5", "Encabezado 5"),
    6: ("Heading 6", "Encabezado 6"),
}


def main() -> None:
    raw = sys.stdin.buffer.read().decode("utf-8")
    try:
        request = json.loads(raw)
        result = handle(request)
        sys.stdout.write(json.dumps(result, ensure_ascii=False))
    except Exception as exc:  # noqa: BLE001 - el host necesita el mensaje, no el tipo Python.
        traceback.print_exc(file=sys.stderr)
        sys.stdout.write(json.dumps({"ok": False, "error": str(exc)}, ensure_ascii=False))
        raise SystemExit(1) from exc


def handle(request: dict[str, Any]) -> dict[str, Any]:
    command = request.get("command")
    if command == "inspect":
        return {"ok": True, "inspection": inspect_document(request["path"])}
    if command == "export":
        export_pdf(request)
        return {"ok": True}
    if command == "apply":
        apply_document(request)
        return {"ok": True}
    raise ValueError(f"Comando no permitido: {command}")


def apply_document(request: dict[str, Any]) -> None:
    path = prepare_document(request)
    kind = request.get("kind") or detect_kind(path)
    operations = request.get("operations") or []
    if kind == "calc":
        apply_calc(path, operations)
    else:
        apply_writer(path, operations)


def prepare_document(request: dict[str, Any]) -> str:
    path = str(Path(request["path"]).resolve())
    kind = request.get("kind") or "writer"
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    if request.get("convertFrom"):
        convert_document(str(Path(request["convertFrom"]).resolve()), path, kind)
    elif request.get("createBlank"):
        if kind == "calc":
            from calc.core import create_spreadsheet

            create_spreadsheet(path)
        else:
            from writer.core import create_document

            create_document(path)
    if not Path(path).is_file():
        raise FileNotFoundError(f"No existe el documento de trabajo: {path}")
    return path


def convert_document(source: str, output: str, kind: str) -> None:
    del kind
    from uno_bridge import load_document, uno_context

    with uno_context() as desktop:
        document = load_document(desktop, source)
        if document is None:
            raise RuntimeError(f"LibreOffice no pudo abrir {source}")
        try:
            document.storeAsURL(Path(output).resolve().as_uri(), ())
        finally:
            document.close(True)


def apply_writer(path: str, operations: list[dict[str, Any]]) -> None:
    from writer import ListItem, WriterSession, WriterTarget

    session = WriterSession(path)
    try:
        for operation in operations:
            kind = operation.get("type")
            if kind == "replace_marker":
                token = "{{" + operation["marker"] + "}}"
                session.replace_text(WriterTarget(kind="text", text=token), operation.get("text") or "")
            elif kind == "insert_heading":
                text = operation.get("text") or ""
                session.insert_text(text)
                apply_heading_style(session.doc, text, int(operation.get("level") or 1))
            elif kind == "insert_paragraph":
                session.insert_text(operation.get("text") or "")
            elif kind == "insert_list":
                items = [ListItem(text=item, level=0) for item in operation.get("items") or []]
                session.insert_list(items, ordered=bool(operation.get("ordered")))
            elif kind == "insert_table":
                data = operation.get("data") or []
                session.insert_table(len(data), len(data[0]), data, name=operation.get("name"))
            elif kind == "update_table":
                session.update_table(WriterTarget(kind="table", name=operation.get("name")), operation.get("data") or [])
            else:
                raise ValueError(f"Operación de Writer no permitida: {kind}")
    except Exception:
        session.close(save=False)
        raise
    else:
        session.close(save=True)


def apply_calc(path: str, operations: list[dict[str, Any]]) -> None:
    from calc import CalcSession, CalcTarget

    session = CalcSession(path)
    try:
        for operation in operations:
            kind = operation.get("type")
            if kind == "set_cell":
                row, col = parse_a1(operation["cell"])
                session.write_cell(
                    CalcTarget(kind="cell", sheet=operation["sheet"], row=row, col=col),
                    coerce(operation.get("value")),
                )
            elif kind == "set_formula":
                row, col = parse_a1(operation["cell"])
                session.write_cell(
                    CalcTarget(kind="cell", sheet=operation["sheet"], row=row, col=col),
                    operation["formula"],
                    value_type="formula",
                )
            elif kind == "write_range":
                row, col = parse_a1(operation["cell"])
                data = [[coerce(cell) for cell in line] for line in operation.get("data") or []]
                session.write_range(
                    CalcTarget(
                        kind="range",
                        sheet=operation["sheet"],
                        row=row,
                        col=col,
                        end_row=row + len(data) - 1,
                        end_col=col + len(data[0]) - 1,
                    ),
                    data,
                )
            elif kind == "add_sheet":
                session.add_sheet(operation["name"])
            elif kind == "rename_sheet":
                session.rename_sheet(CalcTarget(kind="sheet", sheet=operation["sheet"]), operation["name"])
            else:
                raise ValueError(f"Operación de Calc no permitida: {kind}")
        session.recalculate()
    except Exception:
        session.close(save=False)
        raise
    else:
        session.close(save=True)


def export_pdf(request: dict[str, Any]) -> None:
    path = str(Path(request["path"]).resolve())
    pdf = str(Path(request["pdfPath"]).resolve())
    kind = request.get("kind") or detect_kind(path)
    if kind == "calc":
        from calc.core import export_spreadsheet

        export_spreadsheet(path, pdf, "pdf")
    else:
        from writer.core import export_document

        export_document(path, pdf, "pdf")


def inspect_document(path: str) -> dict[str, Any]:
    resolved = str(Path(path).resolve())
    if detect_kind(resolved) == "calc":
        return inspect_calc(resolved)
    return inspect_writer(resolved)


def inspect_writer(path: str) -> dict[str, Any]:
    from writer import WriterSession

    session = WriterSession(path)
    try:
        document = session.doc
        text = document.Text.getString()
        headings: list[dict[str, Any]] = []
        styles: set[str] = set()
        enumeration = document.Text.createEnumeration()
        while enumeration.hasMoreElements():
            paragraph = enumeration.nextElement()
            if not paragraph.supportsService("com.sun.star.text.Paragraph"):
                continue
            style = str(paragraph.ParaStyleName)
            styles.add(style)
            if style.startswith("Heading") or style.startswith("Encabezado"):
                digits = re.findall(r"\d+", style)
                headings.append(
                    {
                        "level": int(digits[-1]) if digits else 1,
                        "text": paragraph.getString(),
                    }
                )
        tables = []
        text_tables = document.getTextTables()
        for index in range(text_tables.getCount()):
            tables.append(text_tables.getByIndex(index).Name)
        return {
            "kind": "writer",
            "text": text,
            "headings": headings,
            "paragraphStyles": sorted(styles),
            "tables": tables,
            "markers": re.findall(r"\{\{([A-Za-z0-9_-]+)\}\}", text),
            "sheets": [],
            "namedRanges": [],
            "formulas": [],
        }
    finally:
        session.close(save=False)


def inspect_calc(path: str) -> dict[str, Any]:
    from calc import CalcSession

    session = CalcSession(path)
    try:
        document = session.doc
        sheets = []
        formulas = []
        text_parts = []
        for index in range(document.Sheets.getCount()):
            sheet = document.Sheets.getByIndex(index)
            sheets.append(sheet.Name)
            formulas.extend(collect_formulas(sheet))
            text_parts.append(sheet.Name)
        return {
            "kind": "calc",
            "text": "\n".join(text_parts),
            "headings": [],
            "paragraphStyles": [],
            "tables": [],
            "markers": [],
            "sheets": sheets,
            "namedRanges": named_ranges(document),
            "formulas": formulas,
        }
    finally:
        session.close(save=False)


def collect_formulas(sheet: Any) -> list[dict[str, str]]:
    found: list[dict[str, str]] = []
    try:
        cursor = sheet.createCursor()
        cursor.gotoStartOfUsedArea(False)
        cursor.gotoEndOfUsedArea(True)
        address = cursor.getRangeAddress()
    except Exception:
        return found
    last_row = min(address.EndRow, address.StartRow + 80)
    last_col = min(address.EndColumn, address.StartColumn + 20)
    for row in range(address.StartRow, last_row + 1):
        for col in range(address.StartColumn, last_col + 1):
            formula = str(sheet.getCellByPosition(col, row).Formula or "")
            if formula.startswith("="):
                found.append({"sheet": sheet.Name, "cell": to_a1(col, row), "formula": formula})
    return found


def named_ranges(document: Any) -> list[str]:
    names: list[str] = []
    try:
        ranges = document.NamedRanges
        for index in range(ranges.getCount()):
            item = ranges.getByIndex(index)
            name = item.getName() if hasattr(item, "getName") else item.Name
            names.append(str(name))
    except Exception:
        return names
    return names


def apply_heading_style(document: Any, text: str, level: int) -> None:
    matches = []
    enumeration = document.Text.createEnumeration()
    while enumeration.hasMoreElements():
        paragraph = enumeration.nextElement()
        if paragraph.supportsService("com.sun.star.text.Paragraph") and paragraph.getString() == text:
            matches.append(paragraph)
    if not matches:
        return
    paragraph = matches[-1]
    for style in HEADING_STYLES.get(level, HEADING_STYLES[1]):
        try:
            paragraph.ParaStyleName = style
            return
        except Exception:
            continue


def detect_kind(path: str) -> str:
    if Path(path).suffix.lower() in {".ods", ".ots", ".xlsx", ".xls"}:
        return "calc"
    return "writer"


def parse_a1(cell: str) -> tuple[int, int]:
    match = re.fullmatch(r"([A-Za-z]{1,3})([1-9][0-9]*)", cell.strip())
    if not match:
        raise ValueError(f"Celda no válida: {cell}")
    letters, row_text = match.groups()
    column = 0
    for character in letters.upper():
        column = column * 26 + (ord(character) - 64)
    return int(row_text) - 1, column - 1


def to_a1(column: int, row: int) -> str:
    number = column + 1
    letters = ""
    while number:
        number, remainder = divmod(number - 1, 26)
        letters = chr(65 + remainder) + letters
    return f"{letters}{row + 1}"


def coerce(value: Any) -> Any:
    if not isinstance(value, str):
        return value
    text = value.strip()
    if re.fullmatch(r"-?\d+", text):
        return int(text)
    if re.fullmatch(r"-?\d+\.\d+", text):
        return float(text)
    return value


if __name__ == "__main__":
    main()
