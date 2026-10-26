namespace DeletionQueue.Core;

public sealed record OverlayTemplate(string Text = "Removal {date} · {quality}", string Position = "bottom", float BandHeight = .16f,
    float FontSize = .055f, string Foreground = "#FFFFFF", string Background = "#8B2635", byte Opacity = 220)
{
    public OverlayLayer[]? Layers { get; init; }
    public static bool ValidColor(string value) => value is { Length: 7 } && value[0] == '#' && value.AsSpan(1).ToArray().All(char.IsAsciiHexDigit);
    public static bool ValidText(string text) => text is { Length: <= 120 } && text.Count(c => c == '\n') <= 3 &&
        !text.Replace("{date}", "").Replace("{quality}", "").Any(c => c is '{' or '}');
    public void Validate()
    {
        if (Text is not { Length: > 0 and <= 120 } || Position is not ("top" or "bottom") ||
            !float.IsFinite(BandHeight) || BandHeight is < .08f or > .4f || !float.IsFinite(FontSize) || FontSize is < .02f or > .12f ||
            !ValidColor(Foreground) || !ValidColor(Background)) throw new QueueException("InvalidOverlayTemplate");
        var remainder = Text.Replace("{date}", "").Replace("{quality}", "");
        if (remainder.Contains('{') || remainder.Contains('}')) throw new QueueException("InvalidOverlayTemplate");
        if (Layers is { Length: > 12 } || Layers?.Select(l => l?.Id).Distinct().Count() != Layers?.Length) throw new QueueException("InvalidOverlayLayers");
        if (Layers is not null) foreach (var layer in Layers) { if (layer is null) throw new QueueException("InvalidOverlayLayers"); layer.Validate(); }
    }
    public string Format(DateTimeOffset? date, bool is4K) => date is null ? "Removal pending · " + (is4K ? "4K" : "Standard") :
        Text.Replace("{date}", date.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).Replace("{quality}", is4K ? "4K" : "Standard");
}
public sealed record OverlayLayer(string Id, string Kind = "text", string Text = "Removal {date}", float X = .05f, float Y = .8f,
    float Width = .9f, float Height = .12f, float Rotation = 0, string Color = "#FFFFFF", byte Opacity = 255,
    float FontSize = .055f, string Align = "center", bool Bold = true)
{
    public void Validate() {
        if (Id is not { Length: > 0 and <= 50 } || !Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') || Kind is not ("text" or "rectangle") ||
            !OverlayTemplate.ValidText(Text) || !OverlayTemplate.ValidColor(Color) || Align is not ("left" or "center" or "right") ||
            !float.IsFinite(X) || !float.IsFinite(Y) || !float.IsFinite(Width) || !float.IsFinite(Height) || !float.IsFinite(Rotation) || !float.IsFinite(FontSize) ||
            X < 0 || Y < 0 || Width is < .04f or > 1 || Height is < .04f or > 1 || X + Width > 1.0001f || Y + Height > 1.0001f ||
            Rotation is < -180 or > 180 || FontSize is < .01f or > .16f) throw new QueueException("InvalidOverlayLayer");
    }
    public string Format(DateTimeOffset? date, bool is4K) => Text.Replace("{date}", date?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "pending").Replace("{quality}", is4K ? "4K" : "Standard");
}
public sealed record MediaPresentationSettings(long Revision = 0, bool CollectionEnabled = false, Guid? CollectionId = null,
    string CollectionName = "Pending removal", bool OverlaysEnabled = false)
{
    public OverlayTemplate Portrait { get; init; } = new();
    public OverlayTemplate Landscape { get; init; } = new(BandHeight: .22f, FontSize: .045f);
    public void Validate() {
        if (Revision < 0 || CollectionName is not { Length: > 0 and <= 100 } || CollectionId == Guid.Empty || Portrait is null || Landscape is null) throw new QueueException("InvalidPresentationSettings");
        Portrait.Validate(); Landscape.Validate();
    }
}
public sealed class ArtworkReceipt
{
    public Guid ItemId { get; set; }
    public int ImageType { get; set; }
    public int Index { get; set; }
    public string OriginalPath { get; set; } = "";
    public string OriginalHash { get; set; } = "";
    public string BackupPath { get; set; } = "";
    public string OverlayPath { get; set; } = "";
    public string OverlayHash { get; set; } = "";
    public string? PreviousOverlayPath { get; set; }
    public string? PreviousOverlayHash { get; set; }
    public string Status { get; set; } = "prepared";
}
public sealed class MediaPresentationState
{
    public MediaPresentationSettings Settings { get; set; } = new();
    public Guid CreationToken { get; set; }
    public Guid? CreatedCollectionId { get; set; }
    // Membership records contain only additions owned by this plugin, never pre-existing members.
    public Dictionary<Guid, HashSet<Guid>> AddedMembers { get; set; } = [];
    public Dictionary<string, ArtworkReceipt> Artwork { get; set; } = [];
}
