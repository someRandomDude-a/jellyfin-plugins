namespace Jellyfin.Plugin.UserManager.Services;

/// <summary>
/// Seerr permission bit flags.
/// </summary>
public static class SeerrPermissions
{
    public const long None = 0;
    public const long Admin = 2;
    public const long ManageSettings = 4;
    public const long ManageUsers = 8;
    public const long ManageRequests = 16;
    public const long Request = 32;
    public const long Vote = 64;
    public const long AutoApprove = 128;
    public const long AutoApproveMovie = 256;
    public const long AutoApproveTv = 512;
    public const long Request4K = 1024;
    public const long Request4KMovie = 2048;
    public const long Request4KTv = 4096;
    public const long RequestAdvanced = 8192;
    public const long RequestView = 16384;
    public const long AutoApprove4K = 32768;
    public const long AutoApprove4KMovie = 65536;
    public const long AutoApprove4KTv = 131072;
    public const long RequestMovie = 262144;
    public const long RequestTv = 524288;
    public const long ManageIssues = 1048576;
    public const long ViewIssues = 2097152;
    public const long CreateIssues = 4194304;
    public const long AutoRequest = 8388608;
    public const long AutoRequestMovie = 16777216;
    public const long AutoRequestTv = 33554432;
    public const long RecentView = 67108864;
    public const long WatchlistView = 134217728;
    public const long ManageBlocklist = 268435456;
    public const long ViewBlocklist = 1073741824;

    /// <summary>
    /// A safe default for invited users: can request movies and TV, but no administration.
    /// </summary>
    public const long DefaultRequester = Request | RequestMovie | RequestTv;
    public static long KnownMask => Catalog.Aggregate(0L, (mask, definition) => mask | definition.Value);
    public static long Merge(long actual, long desired)
    {
        if (desired < 0 || (desired & ~KnownMask) != 0 || (desired & Admin) != 0) throw new SeerrException(400, "Only known non-administrator permissions may be managed.");
        return (actual & ~KnownMask) | desired;
    }

    public static IReadOnlyList<SeerrPermissionDefinition> Catalog { get; } =
    [
        new("Admin", Admin, "Full Seerr administration. Also implies every other permission.", "Management"),
        new("Manage settings", ManageSettings, "Change Seerr settings.", "Management"),
        new("Manage users", ManageUsers, "Create, edit and delete Seerr users.", "Management"),
        new("Manage requests", ManageRequests, "Approve, decline and edit all requests.", "Management"),
        new("Request", Request, "Submit media requests.", "Requests"),
        new("Vote", Vote, "Vote on requests.", "Requests"),
        new("Auto approve", AutoApprove, "Requests are approved automatically.", "Approval"),
        new("Auto approve movies", AutoApproveMovie, "Auto-approve movie requests.", "Approval"),
        new("Auto approve TV", AutoApproveTv, "Auto-approve TV requests.", "Approval"),
        new("Request 4K", Request4K, "Submit 4K requests.", "4K"),
        new("Request 4K movies", Request4KMovie, "Submit 4K movie requests.", "4K"),
        new("Request 4K TV", Request4KTv, "Submit 4K TV requests.", "4K"),
        new("Advanced requests", RequestAdvanced, "Use advanced request options.", "Requests"),
        new("View requests", RequestView, "View requests from other users.", "Requests"),
        new("Auto approve 4K", AutoApprove4K, "Auto-approve 4K requests.", "Approval"),
        new("Auto approve 4K movies", AutoApprove4KMovie, "Auto-approve 4K movie requests.", "Approval"),
        new("Auto approve 4K TV", AutoApprove4KTv, "Auto-approve 4K TV requests.", "Approval"),
        new("Request movies", RequestMovie, "Request movies.", "Requests"),
        new("Request TV", RequestTv, "Request TV shows.", "Requests"),
        new("Manage issues", ManageIssues, "Manage playback issues.", "Issues"),
        new("View issues", ViewIssues, "View playback issues.", "Issues"),
        new("Create issues", CreateIssues, "Report playback issues.", "Issues"),
        new("Auto request", AutoRequest, "Automatically add media to requests.", "Other"),
        new("Auto request movies", AutoRequestMovie, "Automatically request movies.", "Other"),
        new("Auto request TV", AutoRequestTv, "Automatically request TV.", "Other"),
        new("View recently added", RecentView, "View the recently added list.", "Other"),
        new("View watchlist", WatchlistView, "View the watchlist.", "Other"),
        new("Manage blocklist", ManageBlocklist, "Manage the blocklist.", "Other"),
        new("View blocklist", ViewBlocklist, "View the blocklist.", "Other")
    ];

    public static IReadOnlyList<string> Describe(long value)
    {
        var names = Catalog.Where(p => (value & p.Value) == p.Value).Select(p => p.Name).ToList();
        return names.Count == 0 ? ["None"] : names;
    }
}

public sealed record SeerrPermissionDefinition(string Name, long Value, string Description, string Group);
