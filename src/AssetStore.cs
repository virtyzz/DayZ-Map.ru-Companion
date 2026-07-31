namespace CrosshairMarker;

internal sealed class AssetStore
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg"
    };

    private readonly string assetDirectory;

    public AssetStore()
    {
        assetDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppIdentity.DataDirectoryName,
            "assets");
        Directory.CreateDirectory(assetDirectory);
    }

    public string ImportImage(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath);
        if (!AllowedExtensions.Contains(extension))
        {
            throw new InvalidOperationException("Only PNG and JPG images are supported.");
        }

        var fileName = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        var destinationPath = Path.Combine(assetDirectory, fileName);
        File.Copy(sourcePath, destinationPath, overwrite: false);
        return destinationPath;
    }
}
