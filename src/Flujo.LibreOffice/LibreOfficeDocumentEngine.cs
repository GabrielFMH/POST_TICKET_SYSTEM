using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Flujo.Core;
using Flujo.Documents;

namespace Flujo.LibreOffice;

public sealed class LibreOfficeLocator
{
    public static (string Soffice, string Python) Resolve(LibreOfficeOptions options)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var soffice = FirstExisting(
            options.SofficePath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.com"),
            Path.Combine(local, "LibreOffice", "program", "soffice.exe"),
            Path.Combine(local, "LibreOffice", "LibreOffice", "program", "soffice.exe"));
        if (soffice is null)
        {
            throw new ExternalServiceException("No se encontró LibreOffice. Instala la versión de Windows y vuelve a ejecutar verificar-entorno.");
        }

        var program = Path.GetDirectoryName(soffice) ?? "";
        var python = FirstExisting(options.PythonPath, Path.Combine(program, "python.exe"));
        if (python is null)
        {
            throw new ExternalServiceException("LibreOffice está instalado, pero no se encontró python.exe con el módulo UNO.");
        }

        return (soffice, python);
    }

    private static string? FirstExisting(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }
}

public sealed class LibreOfficeDocumentEngine : IDocumentEngine
{
    private readonly LibreOfficeOptions _options;
    private readonly string _contentRoot;

    public LibreOfficeDocumentEngine(LibreOfficeOptions options, string contentRoot)
    {
        _options = options;
        _contentRoot = contentRoot;
    }

    public Task ApplyAsync(DocumentJob job, CancellationToken cancellationToken)
    {
        return InvokeAsync(new BridgeCall
        {
            Command = "apply",
            Kind = KindName(job.Kind),
            Path = job.OutputPath,
            ConvertFrom = job.ConvertFrom,
            CreateBlank = job.CreateBlank,
            Operations = job.Operations.ToList()
        }, cancellationToken);
    }

    public async Task<DocumentInspection> InspectAsync(string path, CancellationToken cancellationToken)
    {
        var response = await InvokeAsync(new BridgeCall
        {
            Command = "inspect",
            Path = path
        }, cancellationToken);
        return response.Inspection ?? throw new ExternalServiceException("LibreOffice no devolvió la inspección del documento.");
    }

    public Task ExportPdfAsync(string sourcePath, string pdfPath, DocumentKind kind, CancellationToken cancellationToken)
    {
        return InvokeAsync(new BridgeCall
        {
            Command = "export",
            Kind = KindName(kind),
            Path = sourcePath,
            PdfPath = pdfPath
        }, cancellationToken);
    }

    private Task<BridgeResult> InvokeAsync(BridgeCall call, CancellationToken cancellationToken)
    {
        return InvokeCoreAsync(call, cancellationToken, allowRetry: true);
    }

    private async Task<BridgeResult> InvokeCoreAsync(BridgeCall call, CancellationToken cancellationToken, bool allowRetry)
    {
        var (soffice, python) = LibreOfficeLocator.Resolve(_options);
        var script = Resolve(_options.BridgeScript);
        var scripts = Resolve(_options.ScriptsDirectory);
        if (!File.Exists(script))
        {
            throw new ExternalServiceException("No se encuentra el puente UNO: " + script);
        }

        var start = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = _contentRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add(script);
        start.Environment["PYTHONPATH"] = scripts;
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["SOFFICE_PATH"] = soffice;
        start.Environment["LIBREOFFICE_PROGRAM_PATH"] = Path.GetDirectoryName(soffice)!;

        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            throw new ExternalServiceException("No se pudo iniciar el intérprete de LibreOffice.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var payload = JsonSerializer.Serialize(call, FlujoJson.Options);
        await process.StandardInput.WriteAsync(payload.AsMemory(), cancellationToken);
        process.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new ExternalServiceException("LibreOffice no terminó dentro del tiempo configurado.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var json = LastJson(stdout);
        if (json is null)
        {
            if (allowRetry && IsTransient(stderr))
            {
                return await InvokeCoreAsync(call, cancellationToken, allowRetry: false);
            }

            throw new ExternalServiceException("El puente UNO no devolvió JSON. " + Trim(stderr));
        }

        var result = JsonSerializer.Deserialize<BridgeResult>(json, FlujoJson.Options)
            ?? throw new ExternalServiceException("No se pudo leer la respuesta del puente UNO.");
        if (process.ExitCode != 0 || !result.Ok)
        {
            var message = result.Error ?? Trim(stderr) ?? "LibreOffice rechazó la operación.";
            if (allowRetry && IsTransient(message))
            {
                return await InvokeCoreAsync(call, cancellationToken, allowRetry: false);
            }

            throw new ExternalServiceException(message);
        }

        return result;
    }

    private static bool IsTransient(string text)
    {
        return text.Contains("disposed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Failed to connect", StringComparison.OrdinalIgnoreCase);
    }

    private string Resolve(string path)
    {
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(_contentRoot, path));
    }

    private static string KindName(DocumentKind kind)
    {
        return kind == DocumentKind.Calc ? "calc" : "writer";
    }

    private static string? LastJson(string stdout)
    {
        foreach (var line in stdout.Split('\n').Reverse())
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
            {
                return trimmed;
            }
        }

        var start = stdout.IndexOf('{');
        var end = stdout.LastIndexOf('}');
        return start >= 0 && end > start ? stdout[start..(end + 1)] : null;
    }

    private static string Trim(string value)
    {
        var compact = value.Trim();
        return compact.Length <= 500 ? compact : compact[^500..];
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed class BridgeCall
    {
        public string Command { get; set; } = "";

        public string? Kind { get; set; }

        public string? Path { get; set; }

        public string? ConvertFrom { get; set; }

        public bool CreateBlank { get; set; }

        public string? PdfPath { get; set; }

        public List<AppliedOperation> Operations { get; set; } = [];
    }

    private sealed class BridgeResult
    {
        public bool Ok { get; set; }

        public string? Error { get; set; }

        public DocumentInspection? Inspection { get; set; }
    }
}
