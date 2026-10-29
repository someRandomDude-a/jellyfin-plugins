namespace DiscordBridge.Api;

// Presentation only. Mutable usernames never select an identity or grant access.
public static class DiscordIdentityLabel
{
    public static string? Username(string? value) => value is { Length: > 0 and <= 80 } && !value.Any(char.IsControl) ? value : null;
    public static string Format(string? username,string id)
    {
        var name=Username(username);
        if(name is null||name==id)return "Discord username unavailable (ID: "+id+")";
        var escaped=string.Concat(name.Select(c=>"\\`*_{}[]()>~|".Contains(c)?"\\"+c:c.ToString()));
        return "@"+escaped+" (ID: "+id+")";
    }
}
