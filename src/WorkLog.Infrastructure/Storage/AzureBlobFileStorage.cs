using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using WorkLog.Application.Abstractions;

namespace WorkLog.Infrastructure.Storage;

/// <summary>Evidencias y soportes en Azure Blob Storage (producción). Contenedor privado.</summary>
internal sealed class AzureBlobFileStorage : IFileStorage
{
    private readonly BlobContainerClient _container;
    private readonly Lazy<Task> _ensure;

    public AzureBlobFileStorage(IOptions<StorageOptions> options)
    {
        var value = options.Value;
        if (string.IsNullOrWhiteSpace(value.ConnectionString))
            throw new InvalidOperationException("Storage:ConnectionString es obligatorio con Storage:Provider = AzureBlob.");

        _container = new BlobContainerClient(value.ConnectionString, value.Container);
        _ensure = new Lazy<Task>(() => _container.CreateIfNotExistsAsync(PublicAccessType.None));
    }

    public async Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct = default)
    {
        await _ensure.Value;
        await _container.GetBlobClient(key).UploadAsync(content,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, ct);
    }

    public async Task<StoredFile?> OpenAsync(string key, CancellationToken ct = default)
    {
        await _ensure.Value;
        var blob = _container.GetBlobClient(key);
        if (!await blob.ExistsAsync(ct)) return null;

        var download = await blob.DownloadStreamingAsync(cancellationToken: ct);
        return new StoredFile(download.Value.Content, download.Value.Details.ContentType, download.Value.Details.ContentLength);
    }
}
