using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CoslyHighPriceBot.Configuration;

namespace CoslyHighPriceBot.Services;

/// <summary>Sends messages to every configured chat via the Telegram Bot API.</summary>
internal sealed class TelegramNotifier(HttpClient http, TelegramOptions options)
{
    /// <summary>
    /// Sends the same text to every chat in Telegram:ChatIds, one request per chat.
    /// Stops at the first failure: a message already delivered to an earlier chat may be
    /// resent on the next run's retry, which is a smaller risk than silently skipping a
    /// destination.
    /// </summary>
    public async Task SendAsync(string text, CancellationToken cancellationToken)
    {
        foreach (var chatId in options.GetChatIds())
            await SendToChatAsync(chatId, text, cancellationToken);
    }

    private async Task SendToChatAsync(string chatId, string text, CancellationToken cancellationToken)
    {
        // The token goes in the URL: it must never end up in a log or an error message.
        var url = $"{options.ApiBaseUrl.TrimEnd('/')}/bot{options.BotToken}/sendMessage";
        var payload = new SendMessageRequest(chatId, text, "HTML", DisableWebPagePreview: true);

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(url, payload, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Could not reach the Telegram API for chat {chatId}: {ex.Message}");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
                return;

            // Telegram's error body usually explains exactly what went wrong
            // (invalid token, unknown chat, malformed HTML).
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Telegram responded {(int)response.StatusCode} {response.ReasonPhrase} for chat {chatId}: {body}");
        }
    }

    private sealed record SendMessageRequest(
        [property: JsonPropertyName("chat_id")] string ChatId,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("parse_mode")] string ParseMode,
        [property: JsonPropertyName("disable_web_page_preview")] bool DisableWebPagePreview);
}
