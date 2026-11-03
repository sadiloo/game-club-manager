using System.Security.Cryptography;
using System.Text.Json;

namespace GameClub.Kiosk.Core;

public sealed class Pbkdf2Hash
{
    public const int DefaultIterations = 600_000;

    public const int MinimumIterations = 100_000;

    public const int MaximumIterations = 5_000_000;

    public const int HashLength = 32;

    public const int SaltLength = 16;

    public Pbkdf2Hash(int iterations, byte[] salt, byte[] hash)
    {
        if (iterations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations));
        }

        if (salt is null || salt.Length < 8)
        {
            throw new ArgumentException("Salt is required.", nameof(salt));
        }

        if (hash is null || hash.Length != HashLength)
        {
            throw new ArgumentException("Hash must be 32 bytes.", nameof(hash));
        }

        Iterations = iterations;
        Salt = salt;
        Hash = hash;
    }

    public int Iterations { get; }

    public byte[] Salt { get; }

    public byte[] Hash { get; }

    public static Pbkdf2Hash Create(string password, byte[]? salt = null, int iterations = DefaultIterations)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Password is required.", nameof(password));
        }

        if (iterations < MinimumIterations || iterations > MaximumIterations)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations));
        }

        var usedSalt = salt ?? RandomSalt();
        if (usedSalt.Length < SaltLength)
        {
            throw new ArgumentException("Salt must be at least 16 bytes.", nameof(salt));
        }

        return new Pbkdf2Hash(iterations, usedSalt, Derive(password, usedSalt, iterations));
    }

    public bool Verify(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        var actual = Derive(password, Salt, Iterations);
        return FixedEquals(actual, Hash);
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(new HashFile
        {
            Iterations = Iterations,
            Salt = Convert.ToBase64String(Salt),
            Hash = Convert.ToBase64String(Hash),
        });
    }

    public static Pbkdf2Hash FromJson(string json)
    {
        var file = JsonSerializer.Deserialize<HashFile>(json) ?? throw new InvalidOperationException("Admin gate file is empty.");
        if (file.Iterations < MinimumIterations || file.Iterations > MaximumIterations)
        {
            throw new InvalidOperationException("Admin hash iteration count is outside the allowed range.");
        }

        var salt = Convert.FromBase64String(file.Salt ?? "");
        if (salt.Length < SaltLength)
        {
            throw new InvalidOperationException("Admin hash salt is too short.");
        }

        var hash = Convert.FromBase64String(file.Hash ?? "");
        return new Pbkdf2Hash(file.Iterations, salt, hash);
    }

    private static byte[] RandomSalt()
    {
        var salt = new byte[SaltLength];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(salt);
        return salt;
    }

    private static byte[] Derive(string password, byte[] salt, int iterations)
    {
#if NETFRAMEWORK
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(HashLength);
#else
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashLength);
#endif
    }

    private static bool FixedEquals(byte[] left, byte[] right)
    {
#if NET8_0_OR_GREATER
        return CryptographicOperations.FixedTimeEquals(left, right);
#else
        var diff = left.Length ^ right.Length;
        var length = Math.Min(left.Length, right.Length);
        for (var i = 0; i < length; i++)
        {
            diff |= left[i] ^ right[i];
        }

        return diff == 0;
#endif
    }

    private sealed class HashFile
    {
        public int Iterations { get; set; }

        public string? Salt { get; set; }

        public string? Hash { get; set; }
    }
}

public sealed class AdminGateSnapshot
{
    public int Failures { get; set; }

    public int Generation { get; set; }

    public string? LockedUntil { get; set; }
}

public sealed class AdminGate
{
    private readonly Pbkdf2Hash _expected;
    private int _failures;
    private int _generation;
    private DateTimeOffset _lockedUntil;

    public AdminGate(Pbkdf2Hash expected)
    {
        _expected = expected ?? throw new ArgumentNullException(nameof(expected));
    }

    public bool IsOpen { get; private set; }

    public DateTimeOffset? OpenUntil { get; private set; }

    public int Generation => _generation;

    public AdminGateSnapshot Snapshot() => new()
    {
        Failures = _failures,
        Generation = _generation,
        LockedUntil = _lockedUntil == default ? null : _lockedUntil.ToString("o"),
    };

    public void Restore(AdminGateSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        _failures = snapshot.Failures < 0 ? 0 : snapshot.Failures;
        _generation = snapshot.Generation < 0 ? 0 : snapshot.Generation;
        if (DateTimeOffset.TryParse(snapshot.LockedUntil, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var until))
        {
            _lockedUntil = until;
        }
    }

    public bool TryOpen(string password, DateTimeOffset now, TimeSpan window)
    {
        if (now < _lockedUntil)
        {
            return false;
        }

        if (!_expected.Verify(password))
        {
            _failures++;
            if (_failures >= 5)
            {
                _generation++;
                var shift = Math.Min(Math.Max(_generation - 1, 0), 6);
                var minutes = 1 << shift;
                if (minutes > 60)
                {
                    minutes = 60;
                }

                _lockedUntil = now.AddMinutes(minutes);
                _failures = 0;
            }

            return false;
        }

        _failures = 0;
        IsOpen = true;
        OpenUntil = now.Add(window);
        return true;
    }

    public bool IsActive(DateTimeOffset now)
    {
        if (!IsOpen || OpenUntil is not DateTimeOffset until || now >= until)
        {
            IsOpen = false;
            OpenUntil = null;
            return false;
        }

        return true;
    }

    public void Close()
    {
        IsOpen = false;
        OpenUntil = null;
    }
}
