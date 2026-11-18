using DerevenkaBot.Core.Application;
using DerevenkaBot.Core.Domain;
using SkiaSharp;

namespace DerevenkaBot.Core.Infrastructure;

public sealed class FakeImageProvider : IImageGenerationProvider
{
    public int Calls { get; private set; }
    public Task<ImageProviderResult> EditAsync(ImageProviderRequest request, CancellationToken ct) => GenerateAsync(request, ct);
    public Task<ImageProviderResult> GenerateAsync(ImageProviderRequest request, CancellationToken ct)
    {
        Calls++;
        ct.ThrowIfCancellationRequested();
        using var image = new SKBitmap(512, 512);
        using var canvas = new SKCanvas(image);
        canvas.Clear(new SKColor(72, 120, 90));
        using var bytes = image.Encode(SKEncodedImageFormat.Png, 100);
        return Task.FromResult(new ImageProviderResult(bytes.ToArray(), "image/png", "fake", "fake-" + request.CorrelationId, ActualUsd: 0));
    }
}
