using System.Security.Cryptography;
using DerevenkaBot.Core.Application;
using DerevenkaBot.Core.Domain;
using SkiaSharp;

namespace DerevenkaBot.Core.Infrastructure;

public sealed class LocalAssetStore(ImageOptions options, ImageDatabase db) : IImageAssetStore
{
    public async Task<StoredAsset> SaveAsync(Stream stream, string? declaredContentType, CancellationToken ct)
    {
        using var bounded = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (bounded.Length + count > options.MaxInputFileMb * 1024L * 1024) throw new InvalidDataException("file_size");
            await bounded.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        bounded.Position = 0;
        using var codec = SKCodec.Create(bounded) ?? throw new InvalidDataException("image_decode");
        var info = codec.Info;
        var mime = codec.EncodedFormat switch { SKEncodedImageFormat.Png => "image/png", SKEncodedImageFormat.Jpeg => "image/jpeg",
            SKEncodedImageFormat.Webp => "image/webp", _ => "" };
        if (mime is not ("image/png" or "image/jpeg" or "image/webp") ||
            declaredContentType != null && declaredContentType != mime ||
            (long)info.Width * info.Height > options.MaxPixels || codec.FrameCount > 1)
            throw new InvalidDataException("image_type_or_dimensions");
        using var decoded = new SKBitmap(info.Width, info.Height);
        if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success) throw new InvalidDataException("image_decode");
        using var image = Orient(decoded, codec.EncodedOrigin);
        using var normalized = image.Encode(SKEncodedImageFormat.Png, 100);
        if (normalized.Size > options.MaxInputFileMb * 1024L * 1024) throw new InvalidDataException("normalized_size");
        var bytes = normalized.ToArray();
        var id = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(options.StoragePath, "assets"));
        var path = AssetPath(id);
        await File.WriteAllBytesAsync(path + ".tmp", bytes, ct);
        File.Move(path + ".tmp", path);
        var asset = new StoredAsset(id, "image/png", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)),
            image.Width, image.Height, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(options.RetentionDays));
        db.Run(s => { s.Assets.Add(asset); return true; });
        return asset;
    }
    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var output = new SKBitmap(swap ? source.Height : source.Width, swap ? source.Width : source.Height);
        using var canvas = new SKCanvas(output);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: canvas.Translate(output.Width, 0); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight: canvas.Translate(output.Width, output.Height); canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft: canvas.Translate(0, output.Height); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop: canvas.RotateDegrees(90); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.RightTop: canvas.Translate(output.Width, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: canvas.Translate(output.Width, output.Height); canvas.RotateDegrees(90); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0, output.Height); canvas.RotateDegrees(270); break;
        }
        canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions());
        return output;
    }
    private string AssetPath(Guid id) => Path.Combine(options.StoragePath, "assets", id.ToString("N") + ".png");
    public Task<Stream> OpenReadAsync(Guid assetId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!db.Run(s => s.Assets.Any(x => x.Id == assetId && x.ExpiresAt > DateTimeOffset.UtcNow)))
            throw new FileNotFoundException("asset_expired");
        return Task.FromResult<Stream>(File.OpenRead(AssetPath(assetId)));
    }
    public Task DeleteAsync(Guid assetId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        db.Run(s =>
        {
            if (s.Generations.Any(x => x.InputAssets.Contains(assetId) || x.ResultAssetId == assetId))
                throw new InvalidOperationException("asset_referenced");
            File.Delete(AssetPath(assetId));
            var asset = s.Assets.Find(x => x.Id == assetId);
            if (asset != null) s.Assets[s.Assets.IndexOf(asset)] = asset with { ExpiresAt = DateTimeOffset.UtcNow };
            return true;
        });
        return Task.CompletedTask;
    }
}
