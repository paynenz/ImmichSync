using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace ImmichSync;

internal sealed record Album(Guid Id, string AlbumName)
{
    public string DisplayName => $"{AlbumName} ({Id.ToString("N")[..8]})";
}

internal sealed class ImmichClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    });

    private readonly Uri api;

    public ImmichClient(Uri server, string apiKey)
    {
        var baseAddress = server.AbsoluteUri.TrimEnd('/') + "/";
        api = new Uri(baseAddress.EndsWith("/api/", StringComparison.Ordinal) ? baseAddress : baseAddress + "api/");
        ApiKey = apiKey;
    }

    private string ApiKey { get; }

    public static Uri ParseServer(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new ArgumentException("Enter an HTTP or HTTPS server address, without a query or fragment.");
        }

        return uri;
    }

    public Task<Album[]> GetAlbumsAsync(CancellationToken token) => GetJsonAsync<Album[]>("albums", token);

    public async Task<HashSet<Guid>> GetPhotoIdsAsync(Guid albumId, CancellationToken token)
    {
        // In v3.4, album metadata no longer embeds assets. The album timeline is grouped by month.
        string query = $"albumId={albumId:D}";
        var buckets = await GetJsonAsync<TimelineBucket[]>($"timeline/buckets?{query}", token);
        var ids = new HashSet<Guid>();
        foreach (var bucket in buckets)
        {
            string date = Uri.EscapeDataString(bucket.TimeBucket);
            var assets = await GetJsonAsync<BucketAssets>($"timeline/bucket?{query}&timeBucket={date}", token);
            if (assets.Id is null || assets.IsImage is null || assets.Id.Length != assets.IsImage.Length)
                throw new InvalidDataException("Immich returned inconsistent album contents.");

            for (int index = 0; index < assets.Id.Length; index++)
            {
                if (assets.IsImage[index])
                    ids.Add(assets.Id[index]);
            }
        }
        return ids;
    }

    public async Task<string?> TryGetOriginalFilenameAsync(Guid id, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var asset = await GetJsonAsync<AssetFilename>($"assets/{id:D}", timeout.Token);
            return string.IsNullOrWhiteSpace(asset.OriginalFileName) ? null : asset.OriginalFileName;
        }
        catch (Exception ex) when (!token.IsCancellationRequested
            && ex is HttpRequestException or IOException or InvalidDataException or JsonException
                or NotSupportedException or OperationCanceledException)
        {
            return null;
        }
    }

    public async Task<(HttpResponseMessage Response, string Filename)> OpenOriginalAsync(Guid id, CancellationToken token)
    {
        using var request = CreateRequest($"assets/{id:D}/original?edited=false");
        var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        try
        {
            EnsureSuccess(response);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidDataException($"Immich returned HTTP {(int)response.StatusCode} for the original file. Expected HTTP 200.");

            string? filename = ReadDownloadFilename(response.Content);
            if (filename is null)
            {
                string contentType = response.Content.Headers.ContentType?.MediaType ?? "unspecified";
                throw new InvalidDataException($"The download response has no usable Content-Disposition filename (Content-Type: {contentType}).");
            }

            return (response, filename);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static string? ReadDownloadFilename(HttpContent content)
    {
        var disposition = content.Headers.ContentDisposition;
        if (!string.IsNullOrWhiteSpace(disposition?.FileNameStar))
            return disposition.FileNameStar;

        // Immich v3.4 leaves parentheses and apostrophes unescaped in its UTF-8 filename.
        const string immichPrefix = "inline; filename*=UTF-8''";
        if (content.Headers.NonValidated.TryGetValues("Content-Disposition", out var values) && values.Count == 1)
        {
            string value = values.Single().Trim();
            if (value.StartsWith(immichPrefix, StringComparison.OrdinalIgnoreCase)
                && !value.AsSpan(immichPrefix.Length).Contains(';'))
            {
                string decoded = Uri.UnescapeDataString(value[immichPrefix.Length..]);
                if (!string.IsNullOrWhiteSpace(decoded) && !decoded.Any(char.IsControl))
                    return decoded;
            }
        }

        string? filename = disposition?.FileName?.Trim('"');
        return string.IsNullOrWhiteSpace(filename) ? null : filename;
    }

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken token)
    {
        using var request = CreateRequest(path);
        using var response = await Http.SendAsync(request, token);
        EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<T>(token)
            ?? throw new InvalidDataException("Immich returned an empty response.");
    }

    private HttpRequestMessage CreateRequest(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(api, path));
        request.Headers.Add("x-api-key", ApiKey);
        return request;
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new HttpRequestException("Immich denied access. Check the API key and its album.read, asset.read and asset.download permissions.",
                null, response.StatusCode);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new HttpRequestException("Immich redirected the request. Use the final server address.", null, response.StatusCode);

        response.EnsureSuccessStatusCode();
    }

    private sealed record TimelineBucket(string TimeBucket);
    private sealed record BucketAssets(Guid[] Id, bool[] IsImage);
    private sealed record AssetFilename(string? OriginalFileName);
}
