using System.Security.Cryptography;
using Flujo.AI;
using Flujo.Core;
using Flujo.Documents;
using Flujo.Instructions;
using Flujo.Quality;
using Flujo.Templates;

namespace Flujo.UnitTests;

public class HarnessTests
{
    [Fact]
    public void Composer_PutsTaskLast()
    {
        var composed = new InstructionComposer().Compose(new InstructionSources
        {
            GlobalText = "tono neutro",
            KindText = "APA",
            ProjectText = "proyecto",
            TemplateText = "plantilla",
            TaskText = "no inventes cifras"
        });

        Assert.Equal(["global", "aplicación", "proyecto", "plantilla", "tarea"], composed.Layers);
        Assert.True(composed.Text.LastIndexOf("no inventes cifras", StringComparison.Ordinal) > composed.Text.IndexOf("tono neutro", StringComparison.Ordinal));
        Assert.Contains("prevalece la que aparece más abajo", composed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void PathGuard_RejectsPathsOutsideTheRoot()
    {
        var root = Directory.CreateTempSubdirectory("flujo-rutas");
        try
        {
            var guard = new PathGuard([root.FullName]);
            var inside = Path.Combine(root.FullName, "archivo.odt");
            Assert.Equal(Path.GetFullPath(inside), guard.RequireAllowed(inside));
            var outside = Path.GetFullPath(Path.Combine(root.FullName, "..", "otro", "archivo.odt"));
            var error = Assert.Throws<PathNotAllowedException>(() => guard.RequireAllowed(outside));
            Assert.Contains("fuera de los directorios permitidos", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void PathGuard_RejectsOverwriteOfTheOriginal()
    {
        var root = Directory.CreateTempSubdirectory("flujo-origen");
        try
        {
            var guard = new PathGuard([root.FullName]);
            var file = Path.Combine(root.FullName, "informe.odt");
            var error = Assert.Throws<PathNotAllowedException>(() => guard.EnsureDistinct(file, file));
            Assert.Contains("documento original", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void Versions_DoNotReuseAnExistingFolder()
    {
        var root = Directory.CreateTempSubdirectory("flujo-versiones");
        try
        {
            var store = new VersionedOutputStore();
            var when = new DateTimeOffset(2026, 10, 8, 21, 0, 0, TimeSpan.Zero);
            var first = store.Create(root.FullName, "Informe", when);
            var second = store.Create(root.FullName, "Informe", when);
            Assert.NotEqual(first, second);
            Assert.True(Directory.Exists(first));
            Assert.True(Directory.Exists(second));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void Manifest_LoadsYamlAndJson()
    {
        var root = Directory.CreateTempSubdirectory("flujo-manifiesto");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "plantilla.odt"), "odt");
            File.WriteAllText(Path.Combine(root.FullName, "manifest.yaml"), """
                name: informe
                kind: writer
                sourceFile: plantilla.odt
                exportPdf: true
                markers:
                  - id: titulo
                    kind: text
                    target: "{{titulo}}"
                checkableRules:
                  - required_markers
                """);
            File.WriteAllText(Path.Combine(root.FullName, "instructions.md"), "tono académico");
            var yaml = new TemplateCatalog().Load(root.FullName);
            Assert.Equal("informe", yaml.Manifest.Name);
            Assert.Equal(DocumentKind.Writer, yaml.Manifest.GetKind());
            Assert.Equal("titulo", yaml.Manifest.Markers[0].Id);
            Assert.Equal("tono académico", yaml.InstructionsText);

            var jsonDir = Directory.CreateTempSubdirectory("flujo-manifiesto-json");
            try
            {
                File.WriteAllText(Path.Combine(jsonDir.FullName, "libro.ods"), "ods");
                File.WriteAllText(Path.Combine(jsonDir.FullName, "manifest.json"), """
                    {
                      "name": "libro",
                      "kind": "calc",
                      "sourceFile": "libro.ods",
                      "allowedFormulas": ["Movimientos!C2"]
                    }
                    """);
                var json = new TemplateCatalog().Load(jsonDir.FullName);
                Assert.Equal(DocumentKind.Calc, json.Manifest.GetKind());
                Assert.Equal("Movimientos!C2", json.Manifest.AllowedFormulas[0]);
            }
            finally
            {
                jsonDir.Delete(true);
            }
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void Parser_RejectsCodeAndInvalidJson()
    {
        var error = Assert.Throws<DocumentValidationException>(() => ModelDocumentParser.Parse("no-json"));
        Assert.Contains("JSON", error.Message, StringComparison.Ordinal);

        var forbidden = Assert.Throws<DocumentValidationException>(() => ModelDocumentParser.Parse("""
            {"title":"x","operations":[{"type":"insert_paragraph","text":"a","script":"print(1)"}]}
            """));
        Assert.Contains("script", forbidden.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_LimitsRegenerationAndFormulas()
    {
        var writer = new TemplateManifest { Name = "informe", Kind = "writer" };
        var missingSection = new ModelDocument
        {
            Operations = [new AppliedOperation { Type = OperationTypes.ReplaceMarker, Text = "texto" }]
        };
        Assert.Throws<DocumentValidationException>(() =>
            OperationValidator.Validate(missingSection, writer, GenerationMode.Regenerate, []));

        var wrongSection = new ModelDocument
        {
            Operations = [new AppliedOperation { Type = OperationTypes.ReplaceMarker, Marker = "otro", Text = "texto" }]
        };
        Assert.Throws<DocumentValidationException>(() =>
            OperationValidator.Validate(wrongSection, writer, GenerationMode.Regenerate, ["desarrollo"]));

        var calc = new TemplateManifest
        {
            Name = "libro",
            Kind = "calc",
            AllowedFormulas = ["Movimientos!C2"]
        };
        var denied = new ModelDocument
        {
            Operations =
            [
                new AppliedOperation
                {
                    Type = OperationTypes.SetFormula,
                    Sheet = "Movimientos",
                    Cell = "C3",
                    Formula = "=SUM(B2)"
                }
            ]
        };
        Assert.Throws<DocumentValidationException>(() =>
            OperationValidator.Validate(denied, calc, GenerationMode.Create, []));

        var dangerous = new ModelDocument
        {
            Operations =
            [
                new AppliedOperation
                {
                    Type = OperationTypes.SetFormula,
                    Sheet = "Movimientos",
                    Cell = "C2",
                    Formula = "=WEBSERVICE(\"http://example.test\")"
                }
            ]
        };
        var formulaError = Assert.Throws<DocumentValidationException>(() =>
            OperationValidator.Validate(dangerous, calc, GenerationMode.Create, []));
        Assert.Contains("WEBSERVICE", formulaError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Quality_DetectsInventedYearsAndMissingSections()
    {
        var report = new QualityChecker().Check(new QualityInput
        {
            Manifest = new TemplateManifest
            {
                RequiredSections = ["1. Introducción"],
                CheckableRules = ["required_sections", "no_invented_references", "heading_numbering", "required_markers"],
                ExportPdf = true
            },
            Inspection = new DocumentInspection
            {
                Text = "Texto (2024).",
                Headings = [new HeadingInfo { Level = 1, Text = "Resultados" }],
                Markers = ["desarrollo"]
            },
            TaskText = "García, M. (2020). Fundamentos.",
            Mode = GenerationMode.Create,
            PdfPath = null
        });

        Assert.False(report.Accepted);
        Assert.Contains(report.Findings, finding => finding.Code == "required_sections");
        Assert.Contains(report.Findings, finding => finding.Code == "no_invented_references");
        Assert.Contains(report.Findings, finding => finding.Code == "heading_numbering");
        Assert.Contains(report.Findings, finding => finding.Code == "required_markers");
        Assert.Contains(report.Findings, finding => finding.Code == "pdf");
        Assert.False(QualityChecker.FormulasMatch("=SUMA(B2;B3)", "=SUM(B2:B3)"));
        Assert.True(QualityChecker.FormulasMatch("=SUMA(B2;B3)", "=SUM(B2,B3)"));
    }

    [Fact]
    public void ExtractJson_IgnoresFencesAndThinking()
    {
        var json = OllamaClient.ExtractJson("<think>interno</think>\n```json\n{\"title\":\"ok\",\"operations\":[]}\n```");
        var model = ModelDocumentParser.Parse(json);
        Assert.Equal("ok", model.Title);
        Assert.Empty(model.Operations);
        Assert.True(OllamaClient.ModelMatches("qwen3.5:latest", "qwen3.5"));
    }

    [Fact]
    public async Task Service_WritesANewVersionAndLeavesTheTemplateUntouched()
    {
        var root = Directory.CreateTempSubdirectory("flujo-servicio");
        try
        {
            var templateDir = Path.Combine(root.FullName, "plantilla");
            Directory.CreateDirectory(templateDir);
            var templateFile = Path.Combine(templateDir, "plantilla.odt");
            await File.WriteAllTextAsync(templateFile, "plantilla-original");
            var before = SHA256.HashData(await File.ReadAllBytesAsync(templateFile));
            var engine = new RecordingEngine();
            var service = new DocumentGenerationService(
                new FixedCatalog(new TemplatePackage
                {
                    Directory = templateDir,
                    Manifest = new TemplateManifest
                    {
                        Name = "informe",
                        Kind = "writer",
                        SourceFile = "plantilla.odt"
                    },
                    InstructionsText = "rellena marcadores"
                }),
                new InstructionComposer(),
                new FixedGenerator(new ModelDocument
                {
                    Title = "Informe",
                    Operations = [new AppliedOperation { Type = OperationTypes.ReplaceMarker, Marker = "titulo", Text = "Informe" }]
                }),
                engine,
                new QualityChecker(),
                new PathGuard([root.FullName]),
                new VersionedOutputStore(),
                new InstructionOptions
                {
                    GlobalPath = Path.Combine(root.FullName, "no-global.md"),
                    WriterPath = Path.Combine(root.FullName, "no-writer.md"),
                    CalcPath = Path.Combine(root.FullName, "no-calc.md")
                },
                new FixedClock());

            var result = await service.RunAsync(new GenerationRequest
            {
                Mode = GenerationMode.Create,
                TemplateDirectory = templateDir,
                OutputRoot = Path.Combine(root.FullName, "output"),
                TaskText = "Material (2020)."
            }, CancellationToken.None);

            Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(templateFile)));
            Assert.NotEqual(templateFile, result.OutputPath);
            Assert.True(File.Exists(result.OutputPath));
            Assert.Equal(result.OutputPath, engine.Job?.OutputPath);
            Assert.Contains("20261008-210000", result.VersionDirectory, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(result.VersionDirectory, "quality-report.json")));
        }
        finally
        {
            root.Delete(true);
        }
    }

    private sealed class FixedCatalog(TemplatePackage package) : ITemplateCatalog
    {
        public TemplatePackage Load(string templateDirectory) => package;
    }

    private sealed class FixedGenerator(ModelDocument document) : IContentGenerator
    {
        public Task EnsureAvailableAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ModelDocument> GenerateAsync(GenerationPrompt prompt, CancellationToken cancellationToken) =>
            Task.FromResult(document);
    }

    private sealed class RecordingEngine : IDocumentEngine
    {
        public DocumentJob? Job { get; private set; }

        public Task ApplyAsync(DocumentJob job, CancellationToken cancellationToken)
        {
            Job = job;
            return Task.CompletedTask;
        }

        public Task<DocumentInspection> InspectAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new DocumentInspection { Kind = "writer", Text = "Informe" });

        public Task ExportPdfAsync(string sourcePath, string pdfPath, DocumentKind kind, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 8, 21, 0, 0, TimeSpan.Zero);
    }
}
