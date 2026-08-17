using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CoslyHighPriceBot.Configuration;

namespace CoslyHighPriceBot.Services;

/// <summary>Sends messages to the configured chat via the Telegram Bot API.</summary>
internal sealed class TelegramNotifier(HttpClient http, TelegramOptions options)
{
    public async Task SendAsync(string text, CancellationToken cancellationToken)
    {
        // The token goes in the URL: it must never end up in a log or an error message.
        var url = $"{options.ApiBaseUrl.TrimEnd('/')}/bot{options.BotToken}/sendMessage";
        var payload = new SendMessageRequest(options.ChatId, text, "HTML", DisableWebPagePreview: true);

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(url, payload, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Could not reach the Telegram API: {ex.Message}");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
                return;

            // Telegram's error body usually explains exactly what went wrong
            // (invalid token, unknown chat, malformed HTML).
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Telegram responded {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }
    }

    private sealed record SendMessageRequest(
        [property: JsonPropertyName("chat_id")] string ChatId,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("parse_mode")] string ParseMode,
        [property: JsonPropertyName("disable_web_page_preview")] bool DisableWebPagePreview);
}
