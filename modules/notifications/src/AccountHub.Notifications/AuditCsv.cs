namespace AccountHub.Notifications;

/// <summary>Export formatting helper for the shared Hub audit panel/writer.</summary>
public static class AuditCsv
{
    public static string Cell(string? value)
    {
        value ??= "";
        var significant = value.TrimStart();
        if (value.Length > 0 && (value[0] is '\t' or '\r' or '\n' || (significant.Length > 0 && significant[0] is '=' or '+' or '-' or '@')))
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
    public static string Row(IEnumerable<string?> values) => string.Join(',', values.Select(Cell)) + "\r\n";
}
