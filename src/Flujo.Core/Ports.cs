using Flujo.Documents;

namespace Flujo.Core;

public sealed record GenerationPrompt(string SystemText, string UserText);

public sealed class InstructionSources
{
    public string? GlobalText { get; init; }

    public string? KindText { get; init; }

    public string? ProjectText { get; init; }

    public string? TemplateText { get; init; }

    public string? TaskText { get; init; }
}

public sealed class ComposedInstructions
{
    public required string Text { get; init; }

    public required IReadOnlyList<string> Layers { get; init; }
}

public interface IContentGenerator
{
    Task EnsureAvailableAsync(CancellationToken cancellationToken);

    Task<ModelDocument> GenerateAsync(GenerationPrompt prompt, CancellationToken cancellationToken);
}

public interface IDocumentEngine
{
    Task ApplyAsync(DocumentJob job, CancellationToken cancellationToken);

    Task<DocumentInspection> InspectAsync(string path, CancellationToken cancellationToken);

    Task ExportPdfAsync(string sourcePath, string pdfPath, DocumentKind kind, CancellationToken cancellationToken);
}

public interface ITemplateCatalog
{
    TemplatePackage Load(string templateDirectory);
}

public interface IInstructionComposer
{
    ComposedInstructions Compose(InstructionSources sources);
}

public sealed class QualityInput
{
    public required TemplateManifest Manifest { get; init; }

    public required DocumentInspection Inspection { get; init; }

    public required string TaskText { get; init; }

    public required GenerationMode Mode { get; init; }

    public IReadOnlyList<string> SectionIds { get; init; } = [];

    public string? PdfPath { get; init; }
}

public interface IQualityChecker
{
    QualityReport Check(QualityInput input);
}
