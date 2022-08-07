﻿using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace GScraper.Google;

/// <summary>
/// Represents a Google Search scraper.
/// </summary>
public class GoogleScraper : IDisposable
{
    /// <summary>
    /// Returns the default API endpoint.
    /// </summary>
    public const string DefaultApiEndpoint = "https://www.google.com/search";

    private readonly HttpClient _httpClient;
    private const string _defaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.114 Safari/537.36";
    private static readonly ReadOnlyMemory<byte> _payloadStart = Encoding.UTF8.GetBytes("AF_initDataCallback({key: 'ds:1'");
    private static readonly ReadOnlyMemory<byte> _payloadEnd = Encoding.UTF8.GetBytes("</script>");
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="GoogleScraper"/> class.
    /// </summary>
    public GoogleScraper()
        : this(new HttpClient(new HttpClientHandler { UseCookies = false })) // Disable cookies so we can set the consent cookie in the request.
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GoogleScraper"/> class using the provided <see cref="HttpClient"/>.
    /// </summary>
    public GoogleScraper(HttpClient client)
    {
        _httpClient = client;
        Init(_httpClient, DefaultApiEndpoint);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GoogleScraper"/> class using the provided <see cref="HttpClient"/> and API endpoint.
    /// </summary>
    [Obsolete("This constructor is deprecated and it will be removed in a future version. Use GoogleScraper(HttpClient) instead.")]
    public GoogleScraper(HttpClient client, string apiEndpoint)
    {
        _httpClient = client;
        Init(_httpClient, apiEndpoint);
    }

    private void Init(HttpClient client, string apiEndpoint)
    {
        GScraperGuards.NotNull(client, nameof(client));
        GScraperGuards.NotNullOrEmpty(apiEndpoint, nameof(apiEndpoint));

        _httpClient.BaseAddress = new Uri(apiEndpoint);

        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(_defaultUserAgent);
        }
    }

    /// <summary>
    /// Gets images from Google Images.
    /// </summary>
    /// <remarks>This method returns at most 100 image results.</remarks>
    /// <param name="query">The search query.</param>
    /// <param name="safeSearch">The safe search level.</param>
    /// <param name="size">The image size.</param>
    /// <param name="color">The image color. <see cref="GoogleImageColors"/> contains the colors that can be used here.</param>
    /// <param name="type">The image type.</param>
    /// <param name="time">The image time.</param>
    /// <param name="license">The image license. <see cref="GoogleImageLicenses"/> contains the licenses that can be used here.</param>
    /// <param name="language">The language code to use. <see cref="GoogleLanguages"/> contains the language codes that can be used here.</param>
    /// <returns>A task representing the asynchronous operation. The result contains an <see cref="IEnumerable{T}"/> of <see cref="GoogleImageResult"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is null or empty.</exception>
    /// <exception cref="GScraperException">An error occurred during the scraping process.</exception>
    public async Task<IEnumerable<GoogleImageResult>> GetImagesAsync(string query, SafeSearchLevel safeSearch = SafeSearchLevel.Off, GoogleImageSize size = GoogleImageSize.Any,
        string? color = null, GoogleImageType type = GoogleImageType.Any, GoogleImageTime time = GoogleImageTime.Any,
        string? license = null, string? language = null)
    {
        GScraperGuards.NotNull(query, nameof(query));

        var uri = new Uri(BuildImageQuery(query, safeSearch, size, color, type, time, license, language), UriKind.Relative);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        // Set the CONSENT cookie in the request to bypass the cookie consent page.
        // This might now work if the scraper is instantiated with a HttpClient handler that has cookies enabled.
        // On newer version of .NET this cookie will be added regardless of the setting mentioned above.
        request.Headers.Add("Cookie", "CONSENT=YES+");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        byte[] page = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

        JsonElement rawImages;
        try
        {
            rawImages = ExtractDataPack(page);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or JsonException)
        {
            throw new GScraperException("Failed to unpack the image object data.", "Google", e);
        }

        return EnumerateResults(rawImages);
    }

    private static IEnumerable<GoogleImageResult> EnumerateResults(JsonElement rawImages)
    {
        if (rawImages.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var rawImage in rawImages.EnumerateArray())
        {
            if (rawImage.FirstOrDefault().GetInt32OrDefault() != 1)
            {
                continue;
            }

            var image = FormatImageObject(rawImage);

            if (image != null)
            {
                yield return image;
            }
        }
    }

    private static JsonElement ExtractDataPack(byte[] page)
    {
        // Extract the JSON data pack from the page.
        int start = page.AsSpan().IndexOf(_payloadStart.Span) - 10;
        int startObject = page.AsSpan(start + 1).IndexOf((byte)'[') + start + 1;
        int end = page.AsSpan(startObject + 1).IndexOf(_payloadEnd.Span) + startObject + 1;
        int endObject = page.AsSpan(0, end).LastIndexOf((byte)']') + 1;
        var rawObject = page.AsMemory(startObject, endObject - startObject);

        var document = JsonDocument.Parse(rawObject);

        return document.RootElement
            .ElementAtOrDefault(31)
            .LastOrDefault()
            .ElementAtOrDefault(12)
            .ElementAtOrDefault(2);
    }

    private static GoogleImageResult? FormatImageObject(JsonElement element)
    {
        var data = element.ElementAtOrDefault(1);
        if (data.ValueKind != JsonValueKind.Array)
            return null;

        var main = data.ElementAtOrDefault(3);
        var info = data.ElementAtOrDefault(9);

        if (info.ValueKind != JsonValueKind.Object)
            info = data.ElementAtOrDefault(11);

        string url = main
            .FirstOrDefault()
            .GetStringOrDefault();

        string title = info
            .GetPropertyOrDefault("2003")
            .ElementAtOrDefault(3)
            .GetStringOrDefault();

        int width = main
            .ElementAtOrDefault(2)
            .GetInt32OrDefault();

        int height = main
            .ElementAtOrDefault(1)
            .GetInt32OrDefault();

        string displayUrl = info
            .GetPropertyOrDefault("2003")
            .ElementAtOrDefault(17)
            .GetStringOrDefault();

        string sourceUrl = info
            .GetPropertyOrDefault("2003")
            .ElementAtOrDefault(2)
            .GetStringOrDefault();

        string thumbnailUrl = data
            .ElementAtOrDefault(2)
            .FirstOrDefault()
            .GetStringOrDefault();

        return new GoogleImageResult(url, title, width, height, displayUrl, sourceUrl, thumbnailUrl);
    }

    private static string BuildImageQuery(string query, SafeSearchLevel safeSearch, GoogleImageSize size, string? color,
        GoogleImageType type, GoogleImageTime time, string? license, string? language)
    {
        string url = $"?q={Uri.EscapeDataString(query)}&tbs=";

        url += size == GoogleImageSize.Any ? ',' : $"isz:{(char)size},";
        url += string.IsNullOrEmpty(color) ? ',' : $"ic:{color},";
        url += type == GoogleImageType.Any ? ',' : $"itp:{type.ToString().ToLowerInvariant()},";
        url += time == GoogleImageTime.Any ? ',' : $"qdr:{(char)time},";
        url += string.IsNullOrEmpty(license) ? "" : $"il:{license}";

        url += "&espv=2" +
               "&biw=1366" +
               "&bih=667" +
               "&site=webhp" +
               "&source=lnms" +
               "&tbm=isch" +
               "&sa=X" +
               "&ei=XosDVaCXD8TasATItgE" +
               "&ved=0CAcQ_AUoAg";

        url += "&safe=" + safeSearch switch
        {
            SafeSearchLevel.Off => "off",
            SafeSearchLevel.Moderate => "medium",
            SafeSearchLevel.Strict => "high",
            _ => throw new ArgumentException("Invalid safe search level.", nameof(safeSearch))
        };

        if (!string.IsNullOrEmpty(language))
            url += $"&lr=lang_{language}&hl={language}";

        return url;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc cref="Dispose()"/>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
            _httpClient.Dispose();

        _disposed = true;
    }
}