using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CoslyHighPriceBot.Configuration;

namespace CoslyHighPriceBot.Services;

/// <summary>Envía mensajes al chat configurado vía la Bot API de Telegram.</summary>
internal sealed class TelegramNotifier(HttpClient http, TelegramOptions options)
{
    public async Task SendAsync(string text, CancellationToken cancellationToken)
    {
        // El token va en la URL: nunca debe terminar en un log ni en un mensaje de error.
        var url = $"{options.ApiBaseUrl.TrimEnd('/')}/bot{options.BotToken}/sendMessage";
        var payload = new SendMessageRequest(options.ChatId, text, "HTML", DisableWebPagePreview: true);

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(url, payload, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"No se pudo contactar a la API de Telegram: {ex.Message}");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
                return;

            // El cuerpo del error de Telegram suele explicar exactamente qué pasó
            // (token inválido, chat inexistente, HTML mal formado).
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Telegram respondió {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }
    }

    private sealed record SendMessageRequest(
        [property: JsonPropertyName("chat_id")] string ChatId,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("parse_mode")] string ParseMode,
        [property: JsonPropertyName("disable_web_page_preview")] bool DisableWebPagePreview);
}
