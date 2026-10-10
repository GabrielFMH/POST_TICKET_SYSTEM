using System.Text.Json;
using Flujo.Core;
using Flujo.Documents;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Flujo.Templates;

public sealed class TemplateCatalog : ITemplateCatalog
{
    private readonly IDeserializer _yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public TemplatePackage Load(string templateDirectory)
    {
        if (!Directory.Exists(templateDirectory))
        {
            throw new TemplateException("No existe el directorio de plantilla: " + templateDirectory);
        }

        var yaml = Path.Combine(templateDirectory, "manifest.yaml");
        var json = Path.Combine(templateDirectory, "manifest.json");
        TemplateManifest manifest;
        if (File.Exists(yaml))
        {
            manifest = _yaml.Deserialize<TemplateManifest>(File.ReadAllText(yaml))
                ?? throw new TemplateException("El manifiesto YAML está vacío.");
        }
        else if (File.Exists(json))
        {
            manifest = JsonSerializer.Deserialize<TemplateManifest>(File.ReadAllText(json), FlujoJson.Options)
                ?? throw new TemplateException("El manifiesto JSON está vacío.");
        }
        else
        {
            throw new TemplateException("La plantilla no tiene manifest.yaml ni manifest.json.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            throw new TemplateException("El manifiesto no tiene nombre.");
        }

        _ = manifest.GetKind();
        if (!string.IsNullOrWhiteSpace(manifest.SourceFile))
        {
            var source = Path.Combine(templateDirectory, manifest.SourceFile);
            if (!File.Exists(source))
            {
                throw new TemplateException("No se encuentra el archivo de plantilla: " + manifest.SourceFile);
            }

            var extension = Path.GetExtension(source);
            var allowed = manifest.GetKind() == DocumentKind.Writer
                ? new[] { ".odt", ".ott", ".docx" }
                : new[] { ".ods", ".ots", ".xlsx" };
            if (!allowed.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                throw new TemplateException("Formato de plantilla no compatible: " + extension);
            }
        }

        var instructionsPath = Path.Combine(templateDirectory, "instructions.md");
        return new TemplatePackage
        {
            Directory = templateDirectory,
            Manifest = manifest,
            InstructionsText = File.Exists(instructionsPath) ? File.ReadAllText(instructionsPath) : ""
        };
    }
}
