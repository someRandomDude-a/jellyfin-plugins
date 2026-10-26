using DeletionQueue.Core;
using SkiaSharp;

namespace Jellyfin.Plugin.DeletionQueue;

public sealed class ArtworkRenderer
{
    public const int MaxBytes = 8 * 1024 * 1024;
    public byte[] Render(byte[] original, OverlayTemplate template, string text, DateTimeOffset? date = null, bool is4K = false)
    {
        template.Validate();
        if (original.Length > MaxBytes || text.Length > 200) throw new QueueException("ArtworkCapacityExceeded");
        using var stream = new SKMemoryStream(original);
        using var codec = SKCodec.Create(stream) ?? throw new QueueException("UnsupportedArtwork");
        if (codec.EncodedOrigin != SKEncodedOrigin.TopLeft) throw new QueueException("UnsupportedArtworkOrientation");
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || info.Width > 8192 || info.Height > 8192 || (long)info.Width * info.Height > 16_000_000) throw new QueueException("ArtworkCapacityExceeded");
        using var bitmap = SKBitmap.Decode(codec) ?? throw new QueueException("UnsupportedArtwork");
        using var canvas = new SKCanvas(bitmap);
        if (template.Layers is not null) foreach (var layer in template.Layers) DrawLayer(canvas, bitmap.Width, bitmap.Height, layer, date, is4K);
        else {
        var band = Math.Max(1, bitmap.Height * template.BandHeight);
        var top = template.Position == "top" ? 0 : bitmap.Height - band;
        using var background = new SKPaint { Color = SKColor.Parse(template.Background).WithAlpha(template.Opacity), IsAntialias = true };
        canvas.DrawRect(0, top, bitmap.Width, band, background);
        using var typeface = SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Bold);
        using var font = new SKFont(typeface, bitmap.Width * template.FontSize);
        var maximum = bitmap.Width * .92f;
        while (font.MeasureText(text) > maximum && font.Size > 8) font.Size *= .9f;
        using var foreground = new SKPaint { Color = SKColor.Parse(template.Foreground), IsAntialias = true };
        var metrics = font.Metrics;
        canvas.DrawText(text, bitmap.Width / 2f, top + band / 2f - (metrics.Ascent + metrics.Descent) / 2f, SKTextAlign.Center, font, foreground);
        }
        using var image = SKImage.FromBitmap(bitmap); using var output = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = output.ToArray();
        if (bytes.Length > MaxBytes) throw new QueueException("ArtworkCapacityExceeded");
        return bytes;
    }
    private static void DrawLayer(SKCanvas canvas, int width, int height, OverlayLayer layer, DateTimeOffset? date, bool is4K) {
        var w = width * layer.Width; var h = height * layer.Height;
        canvas.Save();
        try {
            canvas.Translate(width * layer.X + w / 2, height * layer.Y + h / 2); canvas.RotateDegrees(layer.Rotation);
            var rect = new SKRect(-w / 2, -h / 2, w / 2, h / 2);
            using var paint = new SKPaint { Color = SKColor.Parse(layer.Color).WithAlpha(layer.Opacity), IsAntialias = true };
            if (layer.Kind == "rectangle") { canvas.DrawRect(rect, paint); return; }
            canvas.ClipRect(rect); using var typeface = SKTypeface.FromFamilyName("sans-serif", layer.Bold ? SKFontStyle.Bold : SKFontStyle.Normal);
            using var font = new SKFont(typeface, width * layer.FontSize);
            var lines = layer.Format(date, is4K).Split('\n');
            while (font.Size > 2 && (lines.Any(line => font.MeasureText(line) > w * .94f) || font.Spacing * lines.Length > h * .94f)) font.Size *= .9f;
            var align = layer.Align == "left" ? SKTextAlign.Left : layer.Align == "right" ? SKTextAlign.Right : SKTextAlign.Center;
            var x = layer.Align == "left" ? rect.Left + w * .03f : layer.Align == "right" ? rect.Right - w * .03f : 0;
            var baseline = -(font.Metrics.Ascent + font.Metrics.Descent) / 2 - (lines.Length - 1) * font.Spacing / 2;
            foreach (var line in lines) { canvas.DrawText(line, x, baseline, align, font, paint); baseline += font.Spacing; }
        } finally { canvas.Restore(); }
    }
    public byte[] Preview(OverlayTemplate template, bool portrait, string text, DateTimeOffset? date = null)
    {
        using var bitmap = new SKBitmap(portrait ? 480 : 800, portrait ? 720 : 450);
        using (var canvas = new SKCanvas(bitmap)) {
            using var gradient = SKShader.CreateLinearGradient(new(0, 0), new(bitmap.Width, bitmap.Height), [new(22, 40, 67), new(49, 100, 111), new(18, 30, 49)], null, SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = gradient, IsAntialias = true }; canvas.DrawRect(0, 0, bitmap.Width, bitmap.Height, paint);
            using var glow = new SKPaint { Color = new(218, 219, 186, 105), IsAntialias = true };
            canvas.DrawCircle(bitmap.Width * .72f, bitmap.Height * .28f, bitmap.Width * .12f, glow);
            using var hills = new SKPath(); hills.MoveTo(0, bitmap.Height * .7f); hills.CubicTo(bitmap.Width * .3f, bitmap.Height * .4f, bitmap.Width * .55f, bitmap.Height * .9f, bitmap.Width, bitmap.Height * .52f); hills.LineTo(bitmap.Width, bitmap.Height); hills.LineTo(0, bitmap.Height); hills.Close();
            using var silhouette = new SKPaint { Color = new(14, 26, 44, 170), IsAntialias = true }; canvas.DrawPath(hills, silhouette);
            using var typeface = SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Bold);
            using var font = new SKFont(typeface, bitmap.Width * .05f); using var ink = new SKPaint { Color = new(255, 255, 255, 205), IsAntialias = true };
            canvas.DrawText("YOUR LIBRARY", bitmap.Width * .08f, bitmap.Height * .55f, font, ink);
            font.Size = bitmap.Width * .026f; ink.Color = new(255, 255, 255, 145);
            canvas.DrawText("Original artwork stays yours.", bitmap.Width * .08f, bitmap.Height * .61f, font, ink);
        }
        using var image = SKImage.FromBitmap(bitmap); using var input = image.Encode(SKEncodedImageFormat.Png, 100);
        return Render(input.ToArray(), template, text, date);
    }
}
