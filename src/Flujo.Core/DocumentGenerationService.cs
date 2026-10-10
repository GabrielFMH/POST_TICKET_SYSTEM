using System.Text;
using System.Text.Json;
using Flujo.Documents;

namespace Flujo.Core;

public sealed class DocumentGenerationService
{
    private readonly ITemplateCatalog _templates;
    private readonly IInstructionComposer _instructions;
    private readonly IContentGenerator _generator;
    private readonly IDocumentEngine _engine;
    private readonly IQualityChecker _quality;
    private readonly PathGuard _paths;
    private readonly VersionedOutputStore _versions;
    private readonly TimeProvider _clock;
    private readonly InstructionOptions _instructionPaths;

    public DocumentGenerationService(
        ITemplateCatalog templates,
        IInstructionComposer instructions,
        IContentGenerator generator,
        IDocumentEngine engine,
        IQualityChecker quality,
        PathGuard paths,
        VersionedOutputStore versions,
        InstructionOptions instructionPaths,
        TimeProvider? clock = null)
    {
        _templates = templates;
        _instructions = instructions;
        _generator = generator;
        _engine = engine;
        _quality = quality;
        _paths = paths;
        _versions = versions;
        _instructionPaths = instructionPaths;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<GenerationResult> RunAsync(GenerationRequest request, CancellationToken cancellationToken)
    {
        var templateDirectory = _paths.RequireAllowed(request.TemplateDirectory);
        var template = _templates.Load(templateDirectory);
        var outputRoot = _paths.RequireAllowed(request.OutputRoot);
        var kind = template.Manifest.GetKind();
        var source = ResolveSource(request, template);

        var composed = _instructions.Compose(new InstructionSources
        {
            GlobalText = ReadIfExists(_instructionPaths.GlobalPath),
            KindText = ReadIfExists(kind == DocumentKind.Writer ? _instructionPaths.WriterPath : _instructionPaths.CalcPath),
            ProjectText = ReadOptionalAllowed(request.ProjectInstructionsPath),
            TemplateText = template.InstructionsText,
            TaskText = request.TaskText
        });

        await _generator.EnsureAvailableAsync(cancellationToken);
        var model = await _generator.GenerateAsync(BuildPrompt(request, template, composed), cancellationToken);
        var operations = OperationValidator.Validate(model, template.Manifest, request.Mode, request.SectionIds);

        var versionDirectory = _versions.Create(outputRoot, template.Manifest.Name, _clock.GetUtcNow());
        var nativeExtension = kind == DocumentKind.Writer ? ".odt" : ".ods";
        var outputPath = Path.Combine(versionDirectory, VersionedOutputStore.Sanitize(template.Manifest.Name) + nativeExtension);
        string? convertFrom = null;
        var createBlank = source is null;
        if (source is not null)
        {
            StageSource(source, outputPath, nativeExtension, versionDirectory, out convertFrom);
        }

        await _engine.ApplyAsync(new DocumentJob
        {
            Kind = kind,
            OutputPath = outputPath,
            ConvertFrom = convertFrom,
            CreateBlank = createBlank,
            Operations = operations
        }, cancellationToken);

        var inspection = await _engine.InspectAsync(outputPath, cancellationToken);
        string? pdfPath = null;
        if (template.Manifest.ExportPdf)
        {
            pdfPath = Path.Combine(versionDirectory, VersionedOutputStore.Sanitize(template.Manifest.Name) + ".pdf");
            await _engine.ExportPdfAsync(outputPath, pdfPath, kind, cancellationToken);
            inspection = await _engine.InspectAsync(outputPath, cancellationToken);
        }

        await File.WriteAllTextAsync(
            Path.Combine(versionDirectory, "inspection.json"),
            JsonSerializer.Serialize(inspection, FlujoJson.Options),
            cancellationToken);

        var report = _quality.Check(new QualityInput
        {
            Manifest = template.Manifest,
            Inspection = inspection,
            TaskText = request.TaskText,
            Mode = request.Mode,
            SectionIds = request.SectionIds,
            PdfPath = pdfPath
        });
        var reportPath = Path.Combine(versionDirectory, "quality-report.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, FlujoJson.Options), cancellationToken);
        if (!report.Accepted)
        {
            var detail = string.Join(" ", report.Findings.Select(finding => finding.Message));
            throw new DocumentValidationException($"La comprobación de calidad no pasó. Versión conservada en {versionDirectory}. {detail}");
        }

        return new GenerationResult
        {
            OutputPath = outputPath,
            PdfPath = pdfPath,
            VersionDirectory = versionDirectory,
            Quality = report
        };
    }

    public static GenerationPrompt BuildPrompt(GenerationRequest request, TemplatePackage template, ComposedInstructions instructions)
    {
        var kind = template.Manifest.GetKind();
        var allowed = kind == DocumentKind.Writer
            ? string.Join(", ", OperationTypes.Writer)
            : string.Join(", ", OperationTypes.Calc);
        var system = new StringBuilder();
        system.AppendLine(instructions.Text);
        system.AppendLine();
        system.AppendLine("Responde únicamente con un objeto JSON. No uses markdown ni código ejecutable.");
        system.AppendLine("El objeto tiene title (texto) y operations (arreglo).");
        system.AppendLine("Si la plantilla tiene marcadores, rellénalos con replace_marker o set_cell. No reconstruyas el documento.");
        system.AppendLine("Operaciones permitidas: " + allowed + ".");

        var user = new StringBuilder();
        user.AppendLine("Modo: " + request.Mode);
        user.AppendLine("Tipo: " + kind);
        user.AppendLine("Marcadores:");
        foreach (var marker in template.Manifest.Markers)
        {
            user.AppendLine($"- id={marker.Id}; kind={marker.Kind}; target={marker.Target}");
        }

        if (request.Mode == GenerationMode.Regenerate)
        {
            user.AppendLine("Secciones a regenerar: " + string.Join(", ", request.SectionIds));
            user.AppendLine("Cada operación debe incluir marker con uno de esos identificadores.");
        }

        if (template.Manifest.AllowedFormulas.Count > 0)
        {
            user.AppendLine("Celdas de fórmula autorizadas: " + string.Join(", ", template.Manifest.AllowedFormulas));
        }

        user.AppendLine("Material de la tarea:");
        user.AppendLine(request.TaskText);
        return new GenerationPrompt(system.ToString(), user.ToString());
    }

    private string? ResolveSource(GenerationRequest request, TemplatePackage template)
    {
        if (request.Mode is GenerationMode.Edit or GenerationMode.Regenerate)
        {
            if (string.IsNullOrWhiteSpace(request.ExistingDocument))
            {
                throw new DocumentValidationException("Editar o regenerar requiere el documento existente.");
            }

            return _paths.RequireAllowed(request.ExistingDocument);
        }

        if (string.IsNullOrWhiteSpace(template.Manifest.SourceFile))
        {
            return null;
        }

        return _paths.RequireAllowed(Path.Combine(template.Directory, template.Manifest.SourceFile));
    }

    private void StageSource(string source, string outputPath, string nativeExtension, string versionDirectory, out string? convertFrom)
    {
        var extension = Path.GetExtension(source);
        if (extension.Equals(nativeExtension, StringComparison.OrdinalIgnoreCase))
        {
            _paths.EnsureDistinct(source, outputPath);
            File.Copy(source, outputPath);
            convertFrom = null;
            return;
        }

        var staged = Path.Combine(versionDirectory, "origen" + extension);
        _paths.EnsureDistinct(source, staged);
        File.Copy(source, staged);
        convertFrom = staged;
    }

    private string? ReadOptionalAllowed(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var full = _paths.RequireAllowed(path);
        return File.Exists(full) ? File.ReadAllText(full) : null;
    }

    private string? ReadIfExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var full = _paths.RequireAllowed(Path.GetFullPath(path));
        return File.Exists(full) ? File.ReadAllText(full) : null;
    }
}
