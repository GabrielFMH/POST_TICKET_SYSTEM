using System.Text;
using Flujo.AI;
using Flujo.Core;
using Flujo.Documents;
using Flujo.Instructions;
using Flujo.LibreOffice;
using Flujo.Quality;
using Flujo.Templates;
using Microsoft.Extensions.Configuration;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0 || args[0] is "-h" or "--help" or "ayuda")
{
    PrintHelp();
    return 0;
}

try
{
    var options = LoadOptions();
    var contentRoot = Directory.GetCurrentDirectory();
    return args[0] switch
    {
        "verificar-entorno" => await VerifyAsync(options, contentRoot),
        "generar" => await GenerateAsync(options, contentRoot, args[1..]),
        _ => Unknown(args[0])
    };
}
catch (FlujoException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static int Unknown(string command)
{
    Console.Error.WriteLine("Comando no reconocido: " + command);
    PrintHelp();
    return 1;
}

static void PrintHelp()
{
    Console.WriteLine("""
        Flujo — generación documental local

        flujo verificar-entorno
        flujo generar --modo create|edit|regenerate --plantilla <carpeta> --tarea <archivo.md> [--documento <archivo>] [--secciones id1,id2] [--proyecto <archivo.md>] [--salida output]

        La salida se guarda en una carpeta nueva. Los originales no se modifican.
        """);
}

static HarnessOptions LoadOptions()
{
    var configuration = new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .Build();
    var options = new HarnessOptions();
    configuration.Bind(options);
    if (Environment.GetEnvironmentVariable("OLLAMA_HOST") is { Length: > 0 } host)
    {
        options.Ollama.BaseUrl = host;
    }

    if (Environment.GetEnvironmentVariable("OLLAMA_MODEL") is { Length: > 0 } model)
    {
        options.Ollama.Model = model;
    }

    if (Environment.GetEnvironmentVariable("LIBREOFFICE_PATH") is { Length: > 0 } libreOffice)
    {
        options.LibreOffice.SofficePath = libreOffice;
    }

    return options;
}

static async Task<int> VerifyAsync(HarnessOptions options, string contentRoot)
{
    var failures = 0;
    try
    {
        var (soffice, python) = LibreOfficeLocator.Resolve(options.LibreOffice);
        Console.WriteLine("LibreOffice: " + soffice);
        Console.WriteLine("Python UNO: " + python);
    }
    catch (ExternalServiceException exception)
    {
        Console.WriteLine("LibreOffice: " + exception.Message);
        failures++;
    }

    var script = Path.IsPathRooted(options.LibreOffice.BridgeScript)
        ? options.LibreOffice.BridgeScript
        : Path.Combine(contentRoot, options.LibreOffice.BridgeScript);
    Console.WriteLine(File.Exists(script) ? "Puente: " + script : "Puente no encontrado: " + script);
    if (!File.Exists(script))
    {
        failures++;
    }

    try
    {
        using var http = new HttpClient();
        var client = new OllamaClient(http, options.Ollama);
        await client.EnsureAvailableAsync(CancellationToken.None);
        Console.WriteLine("Ollama: " + options.Ollama.BaseUrl + " modelo " + options.Ollama.Model);
    }
    catch (ExternalServiceException exception)
    {
        Console.WriteLine("Ollama: " + exception.Message);
        failures++;
    }

    return failures == 0 ? 0 : 1;
}

static async Task<int> GenerateAsync(HarnessOptions options, string contentRoot, string[] args)
{
    var values = Parse(args);
    if (!values.TryGetValue("modo", out var modeText) ||
        !values.TryGetValue("plantilla", out var template) ||
        !values.TryGetValue("tarea", out var taskPath))
    {
        throw new DocumentValidationException("generar requiere --modo, --plantilla y --tarea.");
    }

    var mode = modeText.ToLowerInvariant() switch
    {
        "create" or "crear" => GenerationMode.Create,
        "edit" or "editar" => GenerationMode.Edit,
        "regenerate" or "regenerar" => GenerationMode.Regenerate,
        _ => throw new DocumentValidationException("Modo no reconocido: " + modeText)
    };
    var taskFull = Path.GetFullPath(taskPath);
    if (!File.Exists(taskFull))
    {
        throw new DocumentValidationException("No existe el archivo de tarea: " + taskFull);
    }

    var sections = values.TryGetValue("secciones", out var sectionText)
        ? sectionText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : Array.Empty<string>();
    var output = values.TryGetValue("salida", out var outputRoot) ? outputRoot : options.Storage.OutputRoot;
    var roots = options.Storage.AllowedRoots
        .Select(root => Path.GetFullPath(Path.IsPathRooted(root) ? root : Path.Combine(contentRoot, root)))
        .ToList();
    var service = new DocumentGenerationService(
        new TemplateCatalog(),
        new InstructionComposer(),
        new OllamaClient(new HttpClient(), options.Ollama),
        new LibreOfficeDocumentEngine(options.LibreOffice, contentRoot),
        new QualityChecker(),
        new PathGuard(roots),
        new VersionedOutputStore(),
        options.Instructions);

    var result = await service.RunAsync(new GenerationRequest
    {
        Mode = mode,
        TemplateDirectory = Path.GetFullPath(template),
        OutputRoot = Path.GetFullPath(output),
        TaskText = File.ReadAllText(taskFull),
        ExistingDocument = values.TryGetValue("documento", out var document) ? Path.GetFullPath(document) : null,
        ProjectInstructionsPath = values.TryGetValue("proyecto", out var project) ? Path.GetFullPath(project) : null,
        SectionIds = sections
    }, CancellationToken.None);

    Console.WriteLine("Documento: " + result.OutputPath);
    if (result.PdfPath is not null)
    {
        Console.WriteLine("PDF: " + result.PdfPath);
    }

    Console.WriteLine("Versión: " + result.VersionDirectory);
    return 0;
}

static Dictionary<string, string> Parse(string[] args)
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < args.Length; index++)
    {
        var token = args[index];
        if (!token.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
        {
            throw new DocumentValidationException("Argumento no válido: " + token);
        }

        values[token[2..]] = args[++index];
    }

    return values;
}
