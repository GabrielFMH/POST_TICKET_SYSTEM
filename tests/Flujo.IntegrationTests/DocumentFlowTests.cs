using System.Security.Cryptography;
using Flujo.AI;
using Flujo.Core;
using Flujo.Documents;
using Flujo.Instructions;
using Flujo.LibreOffice;
using Flujo.Quality;
using Flujo.Templates;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Flujo.IntegrationTests;

[Collection("LibreOffice")]
public class DocumentFlowTests
{
    [SkippableFact]
    public async Task Writer_CreateEditRegenerate_PreservesTheTemplateAndExportsPdf()
    {
        var root = RepoRoot();
        var (soffice, _) = SkipWithoutLibreOffice();
        var templateDir = Path.Combine(root, "templates", "writer-informe");
        var templateFile = Path.Combine(templateDir, "plantilla.odt");
        var before = SHA256.HashData(await File.ReadAllBytesAsync(templateFile));
        var output = Directory.CreateTempSubdirectory("flujo-writer");
        try
        {
            var service = CreateService(root, soffice, output.FullName, new ScriptedGenerator(
                new ModelDocument
                {
                    Title = "Informe",
                    Operations =
                    [
                        new AppliedOperation { Type = OperationTypes.ReplaceMarker, Marker = "titulo", Text = "Informe de prueba" },
                        new AppliedOperation { Type = OperationTypes.ReplaceMarker, Marker = "introduccion", Text = "La revisión usa el material entregado." },
                        new AppliedOperation { Type = OperationTypes.ReplaceMarker, Marker = "desarrollo", Text = "El desarrollo conserva la estructura de la plantilla." },
                        new AppliedOperation { Type = OperationTypes.ReplaceMarker, Marker = "referencias", Text = "García, M. (2020). Fundamentos. Editorial Sur." },
                        new AppliedOperation
                        {
                            Type = OperationTypes.UpdateTable,
                            Name = "Cifras",
                            Data = [["Indicador", "Valor"], ["Ventas", "10"]]
                        }
                    ]
                },
                new ModelDocument
                {
                    Title = "Informe",
                    Operations =
                    [
                        new AppliedOperation { Type = OperationTypes.InsertParagraph, Text = "Nota de edición sobre el material entregado." }
                    ]
                }));

            var created = await service.RunAsync(Request(GenerationMode.Create, templateDir, output.FullName, WriterTask), CancellationToken.None);
            Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(templateFile)));
            Assert.True(File.Exists(created.PdfPath));
            var createdText = await File.ReadAllTextAsync(Path.Combine(created.VersionDirectory, "inspection.json"));
            Assert.Contains("Informe de prueba", createdText, StringComparison.Ordinal);
            Assert.DoesNotContain("{{", createdText, StringComparison.Ordinal);

            var edited = await service.RunAsync(Request(GenerationMode.Edit, templateDir, output.FullName, WriterTask, created.OutputPath), CancellationToken.None);
            Assert.NotEqual(created.OutputPath, edited.OutputPath);
            Assert.True(File.Exists(edited.PdfPath));

            var regenerated = await CreateService(root, soffice, output.FullName, new ScriptedGenerator(
                new ModelDocument
                {
                    Operations =
                    [
                        new AppliedOperation
                        {
                            Type = OperationTypes.ReplaceMarker,
                            Marker = "desarrollo",
                            Text = "Sección regenerada a partir de García, M. (2020)."
                        }
                    ]
                })).RunAsync(
                Request(GenerationMode.Regenerate, templateDir, output.FullName, WriterTask, templateFile, ["desarrollo"]),
                CancellationToken.None);
            Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(templateFile)));
            var regeneratedText = await File.ReadAllTextAsync(Path.Combine(regenerated.VersionDirectory, "inspection.json"));
            Assert.Contains("Sección regenerada", regeneratedText, StringComparison.Ordinal);
            Assert.Contains("{{titulo}}", regeneratedText, StringComparison.Ordinal);
        }
        finally
        {
            output.Delete(true);
        }
    }

    [SkippableFact]
    public async Task Calc_FillsValuesAndKeepsTheTemplateFormula()
    {
        var root = RepoRoot();
        var (soffice, _) = SkipWithoutLibreOffice();
        var templateDir = Path.Combine(root, "templates", "calc-libro");
        var templateFile = Path.Combine(templateDir, "plantilla.ods");
        var before = SHA256.HashData(await File.ReadAllBytesAsync(templateFile));
        var output = Directory.CreateTempSubdirectory("flujo-calc");
        try
        {
            var service = CreateService(root, soffice, output.FullName, new ScriptedGenerator(
                new ModelDocument
                {
                    Title = "Libro",
                    Operations =
                    [
                        new AppliedOperation { Type = OperationTypes.SetCell, Marker = "ventas", Sheet = "Movimientos", Cell = "B2", Value = "120" },
                        new AppliedOperation { Type = OperationTypes.SetCell, Marker = "costos", Sheet = "Movimientos", Cell = "B3", Value = "45" }
                    ]
                }));
            var created = await service.RunAsync(Request(GenerationMode.Create, templateDir, output.FullName, CalcTask), CancellationToken.None);
            Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(templateFile)));
            Assert.True(File.Exists(created.PdfPath));
            var inspection = await File.ReadAllTextAsync(Path.Combine(created.VersionDirectory, "inspection.json"));
            Assert.Contains("Movimientos", inspection, StringComparison.Ordinal);
            Assert.Contains("B2", inspection, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            output.Delete(true);
        }
    }

    [SkippableFact]
    public async Task Writer_CanBeCreatedFromScratch()
    {
        var root = RepoRoot();
        SkipWithoutLibreOffice();
        var output = Directory.CreateTempSubdirectory("flujo-vacio");
        try
        {
            var engine = new LibreOfficeDocumentEngine(new LibreOfficeOptions(), root);
            var path = Path.Combine(output.FullName, "nuevo.odt");
            await engine.ApplyAsync(new DocumentJob
            {
                Kind = DocumentKind.Writer,
                OutputPath = path,
                CreateBlank = true,
                Operations =
                [
                    new AppliedOperation { Type = OperationTypes.InsertHeading, Level = 1, Text = "1. Alcance" },
                    new AppliedOperation { Type = OperationTypes.InsertParagraph, Text = "Párrafo de prueba." },
                    new AppliedOperation { Type = OperationTypes.InsertList, Ordered = false, Items = ["Uno", "Dos"] },
                    new AppliedOperation
                    {
                        Type = OperationTypes.InsertTable,
                        Name = "Resumen",
                        Data = [["Columna", "Valor"], ["A", "1"]]
                    }
                ]
            }, CancellationToken.None);
            var inspection = await engine.InspectAsync(path, CancellationToken.None);
            Assert.Contains(inspection.Headings, heading => heading.Text == "1. Alcance");
            Assert.Contains("Párrafo de prueba.", inspection.Text, StringComparison.Ordinal);
            Assert.Contains("Resumen", inspection.Tables);
            var pdf = Path.Combine(output.FullName, "nuevo.pdf");
            await engine.ExportPdfAsync(path, pdf, DocumentKind.Writer, CancellationToken.None);
            Assert.True(new FileInfo(pdf).Length > 0);
        }
        finally
        {
            output.Delete(true);
        }
    }

    private const string WriterTask = "Redacta con la referencia García, M. (2020). Fundamentos. Editorial Sur.";
    private const string CalcTask = "Ventas 120 y costos 45. No cambies la fórmula del saldo.";

    private static DocumentGenerationService CreateService(string root, string soffice, string outputRoot, IContentGenerator generator)
    {
        return new DocumentGenerationService(
            new TemplateCatalog(),
            new InstructionComposer(),
            generator,
            new LibreOfficeDocumentEngine(new LibreOfficeOptions { SofficePath = soffice }, root),
            new QualityChecker(),
            new PathGuard([root, outputRoot]),
            new VersionedOutputStore(),
            new InstructionOptions
            {
                GlobalPath = Path.Combine(root, "instructions", "global.md"),
                WriterPath = Path.Combine(root, "instructions", "writer.md"),
                CalcPath = Path.Combine(root, "instructions", "calc.md")
            });
    }

    private static GenerationRequest Request(
        GenerationMode mode,
        string templateDir,
        string outputRoot,
        string task,
        string? document = null,
        IReadOnlyList<string>? sections = null)
    {
        return new GenerationRequest
        {
            Mode = mode,
            TemplateDirectory = templateDir,
            OutputRoot = outputRoot,
            TaskText = task,
            ExistingDocument = document,
            SectionIds = sections ?? []
        };
    }

    private static (string Soffice, string Python) SkipWithoutLibreOffice()
    {
        try
        {
            return LibreOfficeLocator.Resolve(new LibreOfficeOptions());
        }
        catch (ExternalServiceException)
        {
            var portable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LibreOffice", "program", "soffice.exe");
            if (File.Exists(portable))
            {
                return (portable, Path.Combine(Path.GetDirectoryName(portable)!, "python.exe"));
            }

            Skip.If(true, "LibreOffice no está instalado en este equipo.");
            return ("", "");
        }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "bridge", "worker.py")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }

    private sealed class ScriptedGenerator : IContentGenerator
    {
        private readonly Queue<ModelDocument> _documents;

        public ScriptedGenerator(params ModelDocument[] documents)
        {
            _documents = new Queue<ModelDocument>(documents);
        }

        public Task EnsureAvailableAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ModelDocument> GenerateAsync(GenerationPrompt prompt, CancellationToken cancellationToken)
        {
            var document = _documents.Count > 1 ? _documents.Dequeue() : _documents.Peek();
            return Task.FromResult(document);
        }
    }
}
