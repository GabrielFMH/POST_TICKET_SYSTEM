using System.Text.Encodings.Web;

namespace Flujo.Documents;

public enum DocumentKind
{
    Writer,
    Calc
}

public enum GenerationMode
{
    Create,
    Edit,
    Regenerate
}

public sealed class TemplateMarker
{
    public string Id { get; set; } = "";

    public string Kind { get; set; } = "text";

    public string Target { get; set; } = "";
}

public sealed class PreservedFormula
{
    public string Sheet { get; set; } = "";

    public string Cell { get; set; } = "";

    public string Formula { get; set; } = "";
}

public sealed class TemplateManifest
{
    public string Name { get; set; } = "";

    public string Kind { get; set; } = "writer";

    public string? SourceFile { get; set; }

    public bool ExportPdf { get; set; }

    public List<TemplateMarker> Markers { get; set; } = [];

    public List<string> RequiredSections { get; set; } = [];

    public List<string> AllowedFormulas { get; set; } = [];

    public List<string> AllowedReferences { get; set; } = [];

    public List<PreservedFormula> PreservedFormulas { get; set; } = [];

    public List<string> CheckableRules { get; set; } = [];

    public DocumentKind GetKind()
    {
        if (Kind.Equals("calc", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentKind.Calc;
        }

        if (Kind.Equals("writer", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentKind.Writer;
        }

        throw new TemplateException($"Tipo de plantilla no reconocido: {Kind}.");
    }
}

public sealed class TemplatePackage
{
    public required string Directory { get; init; }

    public required TemplateManifest Manifest { get; init; }

    public string InstructionsText { get; init; } = "";
}

public sealed class AppliedOperation
{
    public string Type { get; set; } = "";

    public string? Marker { get; set; }

    public string? Text { get; set; }

    public int? Level { get; set; }

    public bool? Ordered { get; set; }

    public List<string>? Items { get; set; }

    public List<List<string>>? Data { get; set; }

    public string? Name { get; set; }

    public string? Sheet { get; set; }

    public string? Cell { get; set; }

    public string? Formula { get; set; }

    public string? Value { get; set; }
}

public sealed class ModelDocument
{
    public string? Title { get; set; }

    public List<AppliedOperation> Operations { get; set; } = [];
}

public sealed class HeadingInfo
{
    public int Level { get; set; }

    public string Text { get; set; } = "";
}

public sealed class FormulaInfo
{
    public string Sheet { get; set; } = "";

    public string Cell { get; set; } = "";

    public string Formula { get; set; } = "";
}

public sealed class DocumentInspection
{
    public string Kind { get; set; } = "";

    public string Text { get; set; } = "";

    public List<HeadingInfo> Headings { get; set; } = [];

    public List<string> ParagraphStyles { get; set; } = [];

    public List<string> Tables { get; set; } = [];

    public List<string> Markers { get; set; } = [];

    public List<string> Sheets { get; set; } = [];

    public List<string> NamedRanges { get; set; } = [];

    public List<FormulaInfo> Formulas { get; set; } = [];
}

public sealed class QualityFinding
{
    public required string Code { get; init; }

    public required string Message { get; init; }
}

public sealed class QualityReport
{
    public bool Accepted { get; set; }

    public List<QualityFinding> Findings { get; set; } = [];
}

public sealed class GenerationRequest
{
    public required GenerationMode Mode { get; init; }

    public required string TemplateDirectory { get; init; }

    public required string OutputRoot { get; init; }

    public required string TaskText { get; init; }

    public string? ExistingDocument { get; init; }

    public string? ProjectInstructionsPath { get; init; }

    public IReadOnlyList<string> SectionIds { get; init; } = [];
}

public sealed class GenerationResult
{
    public required string OutputPath { get; init; }

    public string? PdfPath { get; init; }

    public required string VersionDirectory { get; init; }

    public required QualityReport Quality { get; init; }
}

public sealed class DocumentJob
{
    public required DocumentKind Kind { get; init; }

    public required string OutputPath { get; init; }

    public string? ConvertFrom { get; init; }

    public bool CreateBlank { get; init; }

    public required IReadOnlyList<AppliedOperation> Operations { get; init; }
}

public static class FlujoJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
