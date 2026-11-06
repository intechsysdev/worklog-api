using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using WorkLog.Application.Abstractions;

namespace WorkLog.Infrastructure.Storage;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>"Local" (disco, desarrollo) o "AzureBlob" (producción).</summary>
    public string Provider { get; set; } = "Local";

    /// <summary>Carpeta raíz para el almacenamiento local.</summary>
    public string LocalPath { get; set; } = "App_Data/files";

    /// <summary>Cadena de conexión de Azure Storage (secreto: variable de entorno o Key Vault).</summary>
    public string? ConnectionString { get; set; }

    public string Container { get; set; } = "worklog";
}

/// <summary>Adaptador de almacenamiento en disco para desarrollo y pruebas.</summary>
internal sealed partial class LocalFileStorage(IOptions<StorageOptions> options) : IFileStorage
{
    private string Root => Path.GetFullPath(options.Value.LocalPath);

    public async Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct = default)
    {
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var file = File.Create(path);
        await content.CopyToAsync(file, ct);
        await File.WriteAllTextAsync(path + ".type", contentType, ct);
    }

    public Task<StoredFile?> OpenAsync(string key, CancellationToken ct = default)
    {
        var path = Resolve(key);
        if (!File.Exists(path)) return Task.FromResult<StoredFile?>(null);

        var type = File.Exists(path + ".type") ? File.ReadAllText(path + ".type") : "application/octet-stream";
        var stream = File.OpenRead(path);
        return Task.FromResult<StoredFile?>(new StoredFile(stream, type, stream.Length));
    }

    /// <summary>Las claves las arma la aplicación; aun así se rechaza cualquier intento de salir de la raíz.</summary>
    private string Resolve(string key)
    {
        if (!SafeKey().IsMatch(key) || key.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Clave de archivo no válida.", nameof(key));

        var path = Path.GetFullPath(Path.Combine(Root, key));
        if (!path.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Clave de archivo no válida.", nameof(key));

        return path;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9/._-]{0,250}$")]
    private static partial Regex SafeKey();
}
