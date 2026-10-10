using System.Text.Json;
using System.Text.RegularExpressions;

namespace Flujo.Documents;

public static class OperationTypes
{
    public const string ReplaceMarker = "replace_marker";
    public const string InsertHeading = "insert_heading";
    public const string InsertParagraph = "insert_paragraph";
    public const string InsertList = "insert_list";
    public const string InsertTable = "insert_table";
    public const string UpdateTable = "update_table";
    public const string SetCell = "set_cell";
    public const string SetFormula = "set_formula";
    public const string WriteRange = "write_range";
    public const string AddSheet = "add_sheet";
    public const string RenameSheet = "rename_sheet";

    public static readonly HashSet<string> Writer =
    [
        ReplaceMarker,
        InsertHeading,
        InsertParagraph,
        InsertList,
        InsertTable,
        UpdateTable
    ];

    public static readonly HashSet<string> Calc =
    [
        SetCell,
        SetFormula,
        WriteRange,
        AddSheet,
        RenameSheet
    ];

    public static readonly string[] ForbiddenPropertyNames =
    [
        "script",
        "code",
        "python",
        "macro",
        "command",
        "exec",
        "eval"
    ];
}

public static class FormulaGuard
{
    private static readonly string[] DeniedFunctions =
    [
        "WEBSERVICE",
        "DDE",
        "CALL",
        "REGISTER.ID",
        "HYPERLINK",
        "IMPORTDATA",
        "FILTERXML"
    ];

    public static void EnsureAllowed(string formula, string cellKey, IReadOnlyCollection<string> allowedCells)
    {
        if (string.IsNullOrWhiteSpace(formula) || !formula.TrimStart().StartsWith('='))
        {
            throw new DocumentValidationException("La fórmula debe empezar por =.");
        }

        var authorized = allowedCells.Any(cell => cell.Equals(cellKey, StringComparison.OrdinalIgnoreCase));
        if (!authorized)
        {
            throw new DocumentValidationException($"La celda {cellKey} no está autorizada para fórmulas.");
        }

        var upper = formula.ToUpperInvariant();
        foreach (var denied in DeniedFunctions)
        {
            if (upper.Contains(denied, StringComparison.Ordinal))
            {
                throw new DocumentValidationException($"La fórmula contiene la función no permitida {denied}.");
            }
        }
    }
}

public static class ModelDocumentParser
{
    public static ModelDocument Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new DocumentValidationException("La respuesta del modelo no es JSON válido.", exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new DocumentValidationException("La respuesta del modelo debe ser un objeto JSON.");
            }

            RejectForbidden(document.RootElement, "$");

            var model = new ModelDocument();
            if (document.RootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
            {
                model.Title = title.GetString();
            }

            if (!document.RootElement.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array)
            {
                throw new DocumentValidationException("La respuesta debe incluir el arreglo operations.");
            }

            foreach (var item in operations.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw new DocumentValidationException("Cada operación debe ser un objeto.");
                }

                model.Operations.Add(ReadOperation(item));
            }

            return model;
        }
    }

    private static AppliedOperation ReadOperation(JsonElement item)
    {
        var operation = new AppliedOperation
        {
            Type = RequireString(item, "type").Trim().ToLowerInvariant(),
            Marker = OptionalString(item, "marker"),
            Text = OptionalString(item, "text"),
            Level = item.TryGetProperty("level", out var level) && level.TryGetInt32(out var levelValue) ? levelValue : null,
            Ordered = item.TryGetProperty("ordered", out var ordered) && ordered.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? ordered.GetBoolean()
                : null,
            Name = OptionalString(item, "name"),
            Sheet = OptionalString(item, "sheet"),
            Cell = OptionalString(item, "cell"),
            Formula = OptionalString(item, "formula"),
            Value = ReadValue(item)
        };

        if (item.TryGetProperty("items", out var items))
        {
            if (items.ValueKind != JsonValueKind.Array)
            {
                throw new DocumentValidationException("items debe ser un arreglo.");
            }

            operation.Items = items.EnumerateArray().Select(element => element.ToString()).ToList();
        }

        if (item.TryGetProperty("data", out var data))
        {
            if (data.ValueKind != JsonValueKind.Array)
            {
                throw new DocumentValidationException("data debe ser un arreglo de filas.");
            }

            operation.Data = data.EnumerateArray().Select(row =>
            {
                if (row.ValueKind != JsonValueKind.Array)
                {
                    throw new DocumentValidationException("Cada fila de data debe ser un arreglo.");
                }

                return row.EnumerateArray().Select(cell => cell.ValueKind == JsonValueKind.String ? cell.GetString() ?? "" : cell.ToString()).ToList();
            }).ToList();
        }

        return operation;
    }

    private static string? ReadValue(JsonElement item)
    {
        if (!item.TryGetProperty("value", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static string RequireString(JsonElement item, string name)
    {
        var value = OptionalString(item, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DocumentValidationException($"La operación no incluye {name}.");
        }

        return value;
    }

    private static string? OptionalString(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static void RejectForbidden(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (OperationTypes.ForbiddenPropertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                {
                    throw new DocumentValidationException($"La respuesta incluye el campo no permitido {property.Name} en {path}.");
                }

                RejectForbidden(property.Value, path + "." + property.Name);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                RejectForbidden(item, path + "[" + index + "]");
                index++;
            }
        }
    }
}

public static class OperationValidator
{
    private static readonly Regex CellPattern = new("^[A-Z]{1,3}[1-9][0-9]*$", RegexOptions.CultureInvariant);

    public static IReadOnlyList<AppliedOperation> Validate(
        ModelDocument document,
        TemplateManifest manifest,
        GenerationMode mode,
        IReadOnlyList<string> sectionIds)
    {
        var kind = manifest.GetKind();
        var allowed = kind == DocumentKind.Writer ? OperationTypes.Writer : OperationTypes.Calc;
        var sections = new HashSet<string>(sectionIds, StringComparer.OrdinalIgnoreCase);
        if (mode == GenerationMode.Regenerate && sections.Count == 0)
        {
            throw new DocumentValidationException("Regenerar requiere al menos una sección.");
        }

        foreach (var operation in document.Operations)
        {
            if (!allowed.Contains(operation.Type))
            {
                throw new DocumentValidationException($"Operación no permitida para {kind}: {operation.Type}.");
            }

            if (mode == GenerationMode.Regenerate &&
                (string.IsNullOrWhiteSpace(operation.Marker) || !sections.Contains(operation.Marker)))
            {
                throw new DocumentValidationException("En regeneración cada operación debe indicar un marcador de las secciones pedidas.");
            }

            ValidateShape(operation, manifest);
        }

        return document.Operations;
    }

    private static void ValidateShape(AppliedOperation operation, TemplateManifest manifest)
    {
        switch (operation.Type)
        {
            case OperationTypes.ReplaceMarker:
                Require(operation.Marker, "marker");
                Require(operation.Text, "text");
                break;
            case OperationTypes.InsertHeading:
                Require(operation.Text, "text");
                if (operation.Level is < 1 or > 6)
                {
                    throw new DocumentValidationException("El nivel de título debe estar entre 1 y 6.");
                }

                break;
            case OperationTypes.InsertParagraph:
                Require(operation.Text, "text");
                break;
            case OperationTypes.InsertList:
                if (operation.Items is null || operation.Items.Count == 0)
                {
                    throw new DocumentValidationException("insert_list requiere items.");
                }

                break;
            case OperationTypes.InsertTable:
            case OperationTypes.UpdateTable:
                Require(operation.Name, "name");
                RequireRectangular(operation.Data);
                break;
            case OperationTypes.SetCell:
                Require(operation.Sheet, "sheet");
                RequireCell(operation.Cell);
                if (operation.Value is null)
                {
                    throw new DocumentValidationException("set_cell requiere value.");
                }

                break;
            case OperationTypes.SetFormula:
                Require(operation.Sheet, "sheet");
                RequireCell(operation.Cell);
                Require(operation.Formula, "formula");
                FormulaGuard.EnsureAllowed(
                    operation.Formula!,
                    operation.Sheet + "!" + operation.Cell,
                    manifest.AllowedFormulas);
                break;
            case OperationTypes.WriteRange:
                Require(operation.Sheet, "sheet");
                RequireCell(operation.Cell);
                RequireRectangular(operation.Data);
                break;
            case OperationTypes.AddSheet:
                Require(operation.Name, "name");
                break;
            case OperationTypes.RenameSheet:
                Require(operation.Sheet, "sheet");
                Require(operation.Name, "name");
                break;
        }
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DocumentValidationException($"Falta el campo {name}.");
        }
    }

    private static void RequireCell(string? cell)
    {
        if (string.IsNullOrWhiteSpace(cell) || !CellPattern.IsMatch(cell.ToUpperInvariant()))
        {
            throw new DocumentValidationException($"Celda no válida: {cell}.");
        }
    }

    private static void RequireRectangular(List<List<string>>? data)
    {
        if (data is null || data.Count == 0 || data[0].Count == 0)
        {
            throw new DocumentValidationException("La tabla o el rango necesita datos.");
        }

        var width = data[0].Count;
        if (data.Any(row => row.Count != width))
        {
            throw new DocumentValidationException("Los datos deben formar un rectángulo.");
        }
    }
}
