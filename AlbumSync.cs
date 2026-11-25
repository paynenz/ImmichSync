using System.IO;
using System.Net;
using System.Net.Http;

namespace ImmichSync;

internal static class AlbumSync
{
    public static async Task RunAsync(SyncSettings settings, IProgress<string> log, CancellationToken token)
    {
        log.Report($"Checking \"{settings.Album.AlbumName}\" for new photos.");
        Directory.CreateDirectory(settings.DownloadFolder);
        var client = new ImmichClient(settings.Server, settings.ApiKey);
        var ids = await client.GetPhotoIdsAsync(settings.Album.Id, token);
        var existing = FindDownloadedPhotos(settings.DownloadFolder, token);
        int downloaded = 0;
        int failed = 0;

        foreach (var id in ids)
        {
            token.ThrowIfCancellationRequested();
            if (existing.Contains(id))
                continue;

            string? originalName = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMinutes(30));
                var original = await client.OpenOriginalAsync(id, timeout.Token);
                using var response = original.Response;
                originalName = original.Filename;
                await SaveOriginalAsync(response, MakeLocalName(originalName, id), settings.DownloadFolder, log, timeout.Token);
                downloaded++;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw;
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                failed++;
                originalName ??= await client.TryGetOriginalFilenameAsync(id, token);
                string photo = originalName is null ? $"{id:D}" : $"\"{originalName.ReplaceLineEndings(" ")}\" ({id:D})";
                log.Report($"Could not download {photo}. {DescribeError(ex)}");
            }
        }

        log.Report($"Check completed. {downloaded} downloaded, {ids.Count - downloaded - failed} already present, {failed} failed.");
    }

    private static HashSet<Guid> FindDownloadedPhotos(string folder, CancellationToken token)
    {
        var ids = new HashSet<Guid>();
        foreach (string path in Directory.EnumerateFiles(folder))
        {
            token.ThrowIfCancellationRequested();
            string name = Path.GetFileName(path);
            int marker = name.LastIndexOf("__", StringComparison.Ordinal);
            int end = marker + 34;
            if (marker >= 0 && end <= name.Length && (end == name.Length || name[end] == '.')
                && Guid.TryParseExact(name.AsSpan(marker + 2, 32), "N", out var id))
            {
                ids.Add(id);
            }
        }
        return ids;
    }

    private static async Task SaveOriginalAsync(HttpResponseMessage response, string name, string folder, IProgress<string> log,
        CancellationToken token)
    {
        log.Report($"Downloading {name}");
        string destination = Path.Combine(folder, name);
        string temporary = Path.Combine(folder, $".immichsync-{Guid.NewGuid():N}.part");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await response.Content.CopyToAsync(output, token);
                if (output.Length == 0 || (response.Content.Headers.ContentLength is long length && output.Length != length))
                    throw new InvalidDataException("The download was incomplete.");
            }

            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination);
        }
        finally
        {
            File.Delete(temporary);
        }

        log.Report($"Downloaded {name}");
    }

    private static string MakeLocalName(string original, Guid id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string name = new string(original.Select(character => invalid.Contains(character) ? '_' : character).ToArray())
            .Trim().TrimEnd('.');
        string extension = Path.GetExtension(name);
        if (extension.Length > 20 || extension.Contains("__", StringComparison.Ordinal))
            extension = "";

        string stem = name[..(name.Length - extension.Length)].TrimEnd(' ', '.');
        if (stem.Length == 0)
            stem = "photo";
        if (stem.Length > 120)
            stem = stem[..(char.IsHighSurrogate(stem[119]) ? 119 : 120)];

        // Windows recognizes device names even when they are followed by a dot.
        string firstSegment = stem.Split('.', 2)[0].TrimEnd().ToUpperInvariant();
        if (firstSegment is "CON" or "PRN" or "AUX" or "NUL"
            || (firstSegment.Length == 4 && (firstSegment.StartsWith("COM") || firstSegment.StartsWith("LPT"))
                && "0123456789¹²³".Contains(firstSegment[3])))
        {
            stem = "_" + stem;
        }

        return $"{stem}__{id:N}{extension}";
    }

    internal static string DescribeError(Exception ex) =>
        ex is OperationCanceledException ? "The request timed out." : ex.Message;
}
