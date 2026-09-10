using System.Net.Http.Headers;
using System.IO.Compression;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// The two things every QWeather call has in common: the credential goes on the request
/// message rather than on the client, and the body arrives gzip-compressed.
///
/// Both matter for correctness, not just for tidiness. The <see cref="HttpClient"/> is shared
/// with the keyless Open-Meteo traffic, so a credential on its default headers would be sent
/// to the other vendor as well; and the vendor compresses by default, so a reader that does
/// not decompress sees bytes that are not JSON.
/// </summary>
internal static class QWeatherHttp
{
    public const string ApiKeyHeaderName = "X-QW-Api-Key";

    public static HttpRequestMessage CreateRequest(Uri uri, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation(ApiKeyHeaderName, apiKey);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        return request;
    }

    /// <summary>
    /// Reads the body, decompressing it when the vendor compressed it. The bound applies to
    /// the decompressed bytes, so a small compressed body cannot expand past it before the
    /// parser sees it.
    /// </summary>
    public static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maxResponseBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResponseBytes);

        bool gzip = content.Headers.ContentEncoding.Any(
            encoding => string.Equals(encoding, "gzip", StringComparison.OrdinalIgnoreCase));
        if (!gzip && content.Headers.ContentLength is { } declared &&
            declared > maxResponseBytes)
        {
            throw new InvalidDataException(
                $"QWeather response exceeds {maxResponseBytes} bytes.");
        }

        await using Stream network = await content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using Stream stream = gzip
            ? new GZipStream(network, CompressionMode.Decompress, leaveOpen: true)
            : network;

        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8 * 1024];
        while (true)
        {
            int read = await stream
                .ReadAsync(chunk, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maxResponseBytes)
            {
                throw new InvalidDataException(
                    $"QWeather response exceeds {maxResponseBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
