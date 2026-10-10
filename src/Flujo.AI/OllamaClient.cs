using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Flujo.Core;
using Flujo.Documents;

namespace Flujo.AI;

public sealed class OllamaClient : IContentGenerator
{
    private static readonly Regex Fence = new("```(?:json)?\\s*(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex Think = new("<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private readonly HttpClient _http;
    private readonly OllamaOptions _options;

    public OllamaClient(HttpClient http, OllamaOptions options)
    {
        _http = http;
        _options = options;
        _http.BaseAddress = new Uri(Normalize(_options.BaseUrl) + "/");

        _http.Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds));
    }

    public async Task EnsureAvailableAsync(CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync("api/tags", cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new ExternalServiceException("No hay conexión con Ollama en " + Normalize(_options.BaseUrl) + ".", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new ExternalServiceException("Ollama respondió " + (int)response.StatusCode + " al consultar los modelos.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            {
                throw new ExternalServiceException("La respuesta de Ollama no incluye modelos.");
            }

            var names = models.EnumerateArray()
                .Select(model => model.TryGetProperty("name", out var name) ? name.GetString() : null)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
            if (!names.Any(name => ModelMatches(name!, _options.Model)))
            {
                throw new ExternalServiceException("El modelo " + _options.Model + " no está en Ollama. Modelos locales: " + string.Join(", ", names) + ".");
            }
        }
    }

    public async Task<ModelDocument> GenerateAsync(GenerationPrompt prompt, CancellationToken cancellationToken)
    {
        var payload = BuildPayload(prompt, includeThink: true);
        var response = await SendAsync(payload, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            response.Dispose();
            response = await SendAsync(BuildPayload(prompt, includeThink: false), cancellationToken);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new ExternalServiceException("Ollama rechazó la generación (" + (int)response.StatusCode + ").");
            }

            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content))
            {
                throw new ExternalServiceException("Ollama no devolvió contenido.");
            }

            var json = ExtractJson(content.GetString() ?? "");
            return ModelDocumentParser.Parse(json);
        }
    }

    public static string ExtractJson(string content)
    {
        var withoutThink = Think.Replace(content, "");
        var fence = Fence.Match(withoutThink);
        var candidate = fence.Success ? fence.Groups[1].Value : withoutThink;
        var start = candidate.IndexOf('{');
        var end = candidate.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new DocumentValidationException("La respuesta del modelo no contiene un objeto JSON.");
        }

        return candidate[start..(end + 1)];
    }

    public static bool ModelMatches(string installed, string requested)
    {
        if (installed.Equals(requested, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var installedBase = installed.Split(':')[0];
        var requestedBase = requested.Split(':')[0];
        return installedBase.Equals(requestedBase, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HttpResponseMessage> SendAsync(object payload, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.PostAsJsonAsync("api/chat", payload, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new ExternalServiceException("Se perdió la conexión con Ollama durante la generación.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ExternalServiceException("Ollama no respondió dentro del tiempo configurado.", exception);
        }
    }

    private object BuildPayload(GenerationPrompt prompt, bool includeThink)
    {
        var format = new
        {
            type = "object",
            properties = new
            {
                title = new { type = "string" },
                operations = new { type = "array" }
            },
            required = new[] { "title", "operations" }
        };

        if (!includeThink)
        {
            return new
            {
                model = _options.Model,
                stream = false,
                format,
                messages = Messages(prompt),
                options = new { temperature = 0.2 }
            };
        }

        return new
        {
            model = _options.Model,
            stream = false,
            think = false,
            format,
            messages = Messages(prompt),
            options = new { temperature = 0.2 }
        };
    }

    private static object[] Messages(GenerationPrompt prompt)
    {
        return
        [
            new { role = "system", content = prompt.SystemText },
            new { role = "user", content = prompt.UserText }
        ];
    }

    private static string Normalize(string url)
    {
        return url.TrimEnd('/');
    }
}
