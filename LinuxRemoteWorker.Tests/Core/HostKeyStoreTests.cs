using LinuxRemoteWorker.Core;

namespace LinuxRemoteWorker.Tests.Core;

public sealed class HostKeyStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lrw-tests-").FullName;
    private readonly HostKeyStore _store;

    private static readonly HostKey Ed25519 = new("ssh-ed25519", "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
    private static readonly HostKey Other = new("ssh-ed25519", "SHA256:BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB");

    public HostKeyStoreTests()
    {
        _store = new HostKeyStore(Path.Combine(_dir, "known_hosts.json"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void An_unknown_server_is_unknown_until_remembered()
    {
        Assert.Equal(HostKeyVerdict.Unknown, _store.Check("203.0.113.10", Ed25519));

        _store.Remember("203.0.113.10", Ed25519);

        Assert.Equal(HostKeyVerdict.Trusted, _store.Check("203.0.113.10", Ed25519));
    }

    [Fact]
    public void A_different_key_for_a_known_server_is_a_change()
    {
        _store.Remember("203.0.113.10", Ed25519);

        Assert.Equal(HostKeyVerdict.Changed, _store.Check("203.0.113.10", Other));
        Assert.Equal(HostKeyVerdict.Changed, _store.Check("203.0.113.10", Ed25519 with { Algorithm = "ecdsa-sha2-nistp256" }));
    }

    [Fact]
    public void Host_names_are_compared_without_case_spaces_or_brackets()
    {
        _store.Remember(" Server.Example.COM ", Ed25519);
        _store.Remember("[2001:db8::1]", Other);

        Assert.Equal(HostKeyVerdict.Trusted, _store.Check("server.example.com", Ed25519));
        Assert.Equal(HostKeyVerdict.Trusted, _store.Check("2001:DB8::1", Other));
    }

    [Fact]
    public void Keys_survive_a_new_store_on_the_same_file()
    {
        _store.Remember("203.0.113.10", Ed25519);

        var known = new HostKeyStore(_store.FilePath).Lookup("203.0.113.10");

        Assert.NotNull(known);
        Assert.Equal(Ed25519, known.Key);
        Assert.True(known.FirstSeenUtc > DateTime.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public void Forgetting_lets_the_next_login_remember_a_new_key()
    {
        _store.Remember("203.0.113.10", Ed25519);

        Assert.True(_store.Forget("203.0.113.10"));
        Assert.False(_store.Forget("203.0.113.10"));
        Assert.Equal(HostKeyVerdict.Unknown, _store.Check("203.0.113.10", Other));
    }

    [Fact]
    public void A_damaged_file_is_an_error_not_an_empty_list()
    {
        File.WriteAllText(_store.FilePath, "{ not json");

        var ex = Assert.Throws<OperationException>(() => _store.Lookup("203.0.113.10"));

        Assert.Contains("known_hosts.json", ex.Message);
    }

    [Fact]
    public void The_change_exception_names_both_keys()
    {
        var ex = new HostKeyChangedException("203.0.113.10", new KnownHostKey(Ed25519.Algorithm, Ed25519.Fingerprint, new DateTime(2026, 10, 1)), Other);

        Assert.Contains(Ed25519.Fingerprint, ex.Message);
        Assert.Contains(Other.Fingerprint, ex.Message);
        Assert.Contains("2026-10-01", ex.Message);
    }
}
