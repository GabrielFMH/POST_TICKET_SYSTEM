using Flujo.AI;
using Flujo.Core;
using Flujo.Documents;

namespace Flujo.IntegrationTests;

public class OllamaTests
{
    [SkippableFact]
    public async Task LocalModel_IsAvailable()
    {
        using var http = new HttpClient();
        var client = new OllamaClient(http, new OllamaOptions());
        try
        {
            await client.EnsureAvailableAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is HttpRequestException or ExternalServiceException)
        {
            Skip.If(true, exception.Message);
        }
    }

    [SkippableFact]
    public async Task LocalModel_ReturnsStructuredJson()
    {
        using var http = new HttpClient();
        var client = new OllamaClient(http, new OllamaOptions { TimeoutSeconds = 180 });
        try
        {
            await client.EnsureAvailableAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is HttpRequestException or ExternalServiceException)
        {
            Skip.If(true, exception.Message);
            return;
        }

        var document = await client.GenerateAsync(
            new GenerationPrompt(
                "Responde solo JSON con title y operations.",
                "Devuelve title igual a prueba y operations como un arreglo vacío."),
            CancellationToken.None);
        Assert.NotNull(document.Operations);
    }
}
