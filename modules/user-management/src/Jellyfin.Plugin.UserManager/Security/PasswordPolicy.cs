namespace Jellyfin.Plugin.UserManager.Security;

/// <summary>
/// Password rules used for self-service signup and resets. Jellyfin still hashes the password;
/// this only prevents weak choices before the account is created.
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 10;
    public const int MaximumLength = 128;

    private static readonly HashSet<string> CommonPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password1", "password123", "1234567890", "qwertyuiop",
        "letmein123", "welcome123", "admin12345", "iloveyou123", "changeme123"
    };

    public static PasswordValidationResult Validate(string? password, string? username = null)
    {
        if (string.IsNullOrEmpty(password))
        {
            return PasswordValidationResult.Fail("Enter a password.");
        }

        if (password.Length < MinimumLength)
        {
            return PasswordValidationResult.Fail($"Use at least {MinimumLength} characters.");
        }

        if (password.Length > MaximumLength)
        {
            return PasswordValidationResult.Fail($"Use at most {MaximumLength} characters.");
        }

        if (CommonPasswords.Contains(password))
        {
            return PasswordValidationResult.Fail("That password is too common.");
        }

        if (!string.IsNullOrWhiteSpace(username)
            && password.Contains(username.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return PasswordValidationResult.Fail("The password must not contain your username.");
        }

        var classes = 0;
        if (password.Any(char.IsLower))
        {
            classes++;
        }

        if (password.Any(char.IsUpper))
        {
            classes++;
        }

        if (password.Any(char.IsDigit))
        {
            classes++;
        }

        if (password.Any(c => !char.IsLetterOrDigit(c)))
        {
            classes++;
        }

        if (classes < 3)
        {
            return PasswordValidationResult.Fail(
                "Use at least three of: lowercase letters, uppercase letters, numbers, symbols.");
        }

        return PasswordValidationResult.Success;
    }

    public static void ThrowIfInvalid(string? password, string? username = null)
    {
        var result = Validate(password, username);
        if (!result.IsValid)
        {
            throw new ArgumentException(result.Error, nameof(password));
        }
    }
}

public sealed record PasswordValidationResult(bool IsValid, string? Error)
{
    public static PasswordValidationResult Success { get; } = new(true, null);

    public static PasswordValidationResult Fail(string error) => new(false, error);
}
