using System.Text;
using Flujo.Core;

namespace Flujo.Instructions;

public sealed class InstructionComposer : IInstructionComposer
{
    public ComposedInstructions Compose(InstructionSources sources)
    {
        var layers = new List<(string Name, string Text)>();
        Add(layers, "global", sources.GlobalText);
        Add(layers, "aplicación", sources.KindText);
        Add(layers, "proyecto", sources.ProjectText);
        Add(layers, "plantilla", sources.TemplateText);
        Add(layers, "tarea", sources.TaskText);

        var builder = new StringBuilder();
        builder.AppendLine("Jerarquía de instrucciones: global, aplicación, proyecto, plantilla y tarea.");
        builder.AppendLine("Si dos capas se contradicen, prevalece la que aparece más abajo.");
        foreach (var layer in layers)
        {
            builder.AppendLine();
            builder.AppendLine("## Instrucciones de " + layer.Name);
            builder.AppendLine(layer.Text.Trim());
        }

        return new ComposedInstructions
        {
            Text = builder.ToString().Trim(),
            Layers = layers.Select(layer => layer.Name).ToList()
        };
    }

    private static void Add(List<(string Name, string Text)> layers, string name, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            layers.Add((name, text));
        }
    }
}
