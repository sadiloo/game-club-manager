using System.Security.Cryptography;
using System.Text.Json;

namespace GameClub.Kiosk.Core;

public sealed class Pbkdf2Hash
{
    public const int DefaultIterations = 100_000;
    public const int HashLength = 32;

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

        var usedSalt = salt ?? RandomSalt();
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
        return new Pbkdf2Hash(file.Iterations, Convert.FromBase64String(file.Salt ?? ""), Convert.FromBase64String(file.Hash ?? ""));
    }

    private static byte[] RandomSalt()
    {
        var salt = new byte[16];
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
        var diff = left.Length ^ right.Length;
        var length = Math.Min(left.Length, right.Length);
        for (var i = 0; i < length; i++)
        {
            diff |= left[i] ^ right[i];
        }

        return diff == 0;
    }

    private sealed class HashFile
    {
        public int Iterations { get; set; }

        public string? Salt { get; set; }

        public string? Hash { get; set; }
    }
}

public sealed class AdminGate
{
    private readonly Pbkdf2Hash _expected;
    private int _failures;
    private DateTimeOffset _lockedUntil;

    public AdminGate(Pbkdf2Hash expected)
    {
        _expected = expected ?? throw new ArgumentNullException(nameof(expected));
    }

    public bool IsOpen { get; private set; }

    public DateTimeOffset? OpenUntil { get; private set; }

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
                _lockedUntil = now.AddMinutes(1);
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
