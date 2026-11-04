using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClickSpares.SparesExplorer.Services;

public sealed record ManagedUser(string Email, string PasswordHash, bool IsAdmin, bool Enabled, string SecurityStamp);
public sealed record UserSummary(string Email, bool IsAdmin, bool Enabled);
public sealed record AuthenticatedUser(string Email, bool IsAdmin, string SecurityStamp);

/// <summary>Server-local accounts; credentials and keys must never enter a release or Git.</summary>
public sealed class UserStore(IWebHostEnvironment environment)
{
    private readonly SemaphoreSlim mutex = new(1, 1);
    private readonly string path = Path.Combine(environment.ContentRootPath, "Data", "Auth", "users.json");
    private static readonly Regex EmailPattern = new(@"^[^@\s]{1,64}@[^@\s]{1,190}$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string DummyHash = $"pbkdf2-sha256:210000:{Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))}:{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}";
    private List<ManagedUser> users = [];

    public static string NormaliseEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    private static void ValidateEmail(string email)
    {
        if (email.Length > 254 || !EmailPattern.IsMatch(email) || email.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Enter a valid email address.");
        if (!AdminAccountScope.IsStaff(email))
            throw new ArgumentException("This explorer currently allows Click Spares staff accounts only (@clickspares.co.uk).");
    }

    private static string HashPassword(string password)
    {
        if (password.Length < 12 || password.Length > 256)
            throw new ArgumentException("Passwords must contain between 12 and 256 characters.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256:210000:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string encoded)
    {
        if (password.Length > 256) return false;
        try
        {
            var parts = encoded.Split(':');
            if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) ||
                iterations < 100_000 || iterations > 2_000_000) return false;
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            if (salt.Length != 16 || expected.Length != 32) return false;
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    private async Task LoadAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var loaded = File.Exists(path)
            ? JsonSerializer.Deserialize<List<ManagedUser>>(await File.ReadAllTextAsync(path))
                ?? throw new InvalidDataException("The account database is invalid.")
            : [];
        if (loaded.Any(user => user is null || string.IsNullOrWhiteSpace(user.Email) ||
                string.IsNullOrWhiteSpace(user.PasswordHash) || string.IsNullOrWhiteSpace(user.SecurityStamp)) ||
            loaded.Select(user => NormaliseEmail(user.Email)).Distinct(StringComparer.Ordinal).Count() != loaded.Count)
            throw new InvalidDataException("The account database is invalid.");
        users = loaded.Select(user => user with { Email = NormaliseEmail(user.Email) }).ToList();
    }

    private async Task SaveAsync()
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(users, JsonOptions));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void RequireAdminAccess(string actingAdminEmail, string targetEmail)
    {
        var actor = NormaliseEmail(actingAdminEmail);
        if (!users.Any(user => user.Email == actor && user.IsAdmin && user.Enabled) ||
            !AdminAccountScope.CanManage(actor, targetEmail))
            throw new ArgumentException("You do not have permission to manage that account.");
    }

    public async Task<IReadOnlyList<UserSummary>> ListForAdminAsync(string actingAdminEmail)
    {
        await mutex.WaitAsync();
        try
        {
            await LoadAsync();
            RequireAdminAccess(actingAdminEmail, actingAdminEmail);
            return users.Where(user => AdminAccountScope.CanManage(actingAdminEmail, user.Email))
                .OrderBy(user => user.Email, StringComparer.OrdinalIgnoreCase)
                .Select(user => new UserSummary(user.Email, user.IsAdmin, user.Enabled)).ToArray();
        }
        finally { mutex.Release(); }
    }

    /// <summary>Local CLI provisioning only. The first administrator has no default password.</summary>
    public async Task CreateFirstAdministratorAsync(string email, string password)
    {
        email = NormaliseEmail(email);
        ValidateEmail(email);
        var hash = HashPassword(password);
        await mutex.WaitAsync();
        try
        {
            await LoadAsync();
            if (users.Count != 0)
                throw new ArgumentException("Accounts already exist. Sign in as an administrator to manage users.");
            users.Add(new ManagedUser(email, hash, true, true, Guid.NewGuid().ToString("N")));
            await SaveAsync();
        }
        finally { mutex.Release(); }
    }

    public async Task CreateForAdminAsync(string actingAdminEmail, string email, string password, bool admin)
    {
        email = NormaliseEmail(email);
        ValidateEmail(email);
        var hash = HashPassword(password);
        await mutex.WaitAsync();
        try
        {
            await LoadAsync();
            RequireAdminAccess(actingAdminEmail, email);
            if (users.Any(user => user.Email == email)) throw new ArgumentException("That email address already has an account.");
            users.Add(new ManagedUser(email, hash, admin, true, Guid.NewGuid().ToString("N")));
            await SaveAsync();
        }
        finally { mutex.Release(); }
    }

    public async Task ResetPasswordForAdminAsync(string actingAdminEmail, string email, string password)
    {
        email = NormaliseEmail(email);
        var hash = HashPassword(password);
        await mutex.WaitAsync();
        try
        {
            await LoadAsync();
            RequireAdminAccess(actingAdminEmail, email);
            var index = users.FindIndex(user => user.Email == email);
            if (index < 0) throw new ArgumentException("User not found.");
            users[index] = users[index] with { PasswordHash = hash, SecurityStamp = Guid.NewGuid().ToString("N") };
            await SaveAsync();
        }
        finally { mutex.Release(); }
    }

    public async Task SetEnabledForAdminAsync(string actingAdminEmail, string email, bool enabled)
    {
        email = NormaliseEmail(email);
        await mutex.WaitAsync();
        try
        {
            await LoadAsync();
            RequireAdminAccess(actingAdminEmail, email);
            var index = users.FindIndex(user => user.Email == email);
            if (index < 0) throw new ArgumentException("User not found.");
            if (!enabled && email == NormaliseEmail(actingAdminEmail))
                throw new ArgumentException("You cannot disable your own account.");
            if (!enabled && users[index].IsAdmin && users[index].Enabled &&
                users.Count(user => user.IsAdmin && user.Enabled && AdminAccountScope.IsStaff(user.Email)) <= 1)
                throw new ArgumentException("You cannot disable the final enabled administrator.");
            users[index] = users[index] with { Enabled = enabled, SecurityStamp = Guid.NewGuid().ToString("N") };
            await SaveAsync();
        }
        finally { mutex.Release(); }
    }

    public async Task DeleteAsync(string email, string actingAdminEmail)
    {
        email = NormaliseEmail(email);
        actingAdminEmail = NormaliseEmail(actingAdminEmail);
        if (email.Length == 0 || actingAdminEmail.Length == 0)
            throw new ArgumentException("An account and an acting administrator are required.");
        if (email == actingAdminEmail) throw new ArgumentException("You cannot delete your own account.");
        await mutex.WaitAsync();
        try
        {
            await LoadAsync();
            RequireAdminAccess(actingAdminEmail, email);
            var index = users.FindIndex(user => user.Email == email);
            if (index < 0) throw new ArgumentException("User not found.");
            if (users[index].IsAdmin && users[index].Enabled &&
                users.Count(user => user.IsAdmin && user.Enabled && AdminAccountScope.IsStaff(user.Email)) <= 1)
                throw new ArgumentException("You cannot delete the final enabled administrator.");
            users.RemoveAt(index);
            await SaveAsync();
        }
        finally { mutex.Release(); }
    }

    public async Task<AuthenticatedUser?> AuthenticateAsync(string email, string password)
    {
        var normalised = NormaliseEmail(email);
        await mutex.WaitAsync();
        try
        {
            await LoadAsync();
            var entry = users.FirstOrDefault(user => user.Email == normalised && user.Enabled && AdminAccountScope.IsStaff(user.Email));
            // Unknown/disabled accounts perform the same expensive comparison as an incorrect password.
            var verified = VerifyPassword(password, entry?.PasswordHash ?? DummyHash);
            return entry is not null && verified ? new AuthenticatedUser(entry.Email, entry.IsAdmin, entry.SecurityStamp) : null;
        }
        finally { mutex.Release(); }
    }

    public async Task<bool> IsCurrentAsync(string email, string securityStamp, bool? claimedAdmin = null)
    {
        await mutex.WaitAsync();
        try
        {
            await LoadAsync();
            return users.Any(user => user.Email == NormaliseEmail(email) && user.Enabled &&
                AdminAccountScope.IsStaff(user.Email) && user.SecurityStamp == securityStamp &&
                (!claimedAdmin.HasValue || user.IsAdmin == claimedAdmin.Value));
        }
        finally { mutex.Release(); }
    }
}
