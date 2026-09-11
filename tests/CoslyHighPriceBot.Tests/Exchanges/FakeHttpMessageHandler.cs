using System.Net;
using System.Text;

namespace CoslyHighPriceBot.Tests.Exchanges;

/// <summary>
/// Serves canned JSON bodies to an HttpClient without touching the network, keyed by a
/// substring of the request URL — enough to tell a ticker call apart from a catalog call
/// (and, for Bybit, a first page from a page fetched with a cursor).
/// </summary>
internal sealed class FakeHttpMessageHandler(List<string>? capturedRequestBodies, params (string UrlContains, string Body)[] responses) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();

        if (capturedRequestBodies is not null && request.Content is not null)
            capturedRequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));

        foreach (var (urlContains, body) in responses)
        {
            if (url.Contains(urlContains, StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }
        }

        throw new InvalidOperationException($"FakeHttpMessageHandler has no canned response for {url}.");
    }

    public static HttpClient CreateClient(params (string UrlContains, string Body)[] responses) =>
        new(new FakeHttpMessageHandler(null, responses));

    public static HttpClient CreateClient(List<string> capturedRequestBodies, params (string UrlContains, string Body)[] responses) =>
        new(new FakeHttpMessageHandler(capturedRequestBodies, responses));
}
