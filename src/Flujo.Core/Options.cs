namespace Flujo.Core;

public sealed class OllamaOptions
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:11434";

    public string Model { get; set; } = "qwen3.5:latest";

    public int TimeoutSeconds { get; set; } = 180;
}

public sealed class LibreOfficeOptions
{
    public string? SofficePath { get; set; }

    public string? PythonPath { get; set; }

    public string BridgeScript { get; set; } = "bridge/worker.py";

    public string ScriptsDirectory { get; set; } = "bridge/vendor/libreoffice-skills/scripts";

    public int TimeoutSeconds { get; set; } = 180;
}

public sealed class StorageOptions
{
    public List<string> AllowedRoots { get; set; } = ["."];

    public string OutputRoot { get; set; } = "output";
}

public sealed class InstructionOptions
{
    public string GlobalPath { get; set; } = "instructions/global.md";

    public string WriterPath { get; set; } = "instructions/writer.md";

    public string CalcPath { get; set; } = "instructions/calc.md";
}

public sealed class HarnessOptions
{
    public OllamaOptions Ollama { get; set; } = new();

    public LibreOfficeOptions LibreOffice { get; set; } = new();

    public StorageOptions Storage { get; set; } = new();

    public InstructionOptions Instructions { get; set; } = new();
}
