using System.Net;
using Elio.Sync;

namespace Elio.Tests;

public sealed class SyncPairingTests
{
    private static readonly Uri Server = new("https://sync.invalid/");

    [Theory]
    [InlineData("ABCDE-FGHJK", "ABCDEFGHJK")]
    [InlineData("abcde-fghjk", "ABCDEFGHJK")]
    [InlineData("  abcde fghjk  ", "ABCDEFGHJK")]
    [InlineData("a-b-c-d-e-f-g-h-j-k", "ABCDEFGHJK")]
    [InlineData("23456\t789AB", "23456789AB")]
    public void Normalize_AcceptsWhatAUserWouldType(string typed, string expected) =>
        Assert.Equal(expected, SyncPairing.Normalize(typed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABCDE-FGHJ")] // nine
    [InlineData("ABCDE-FGHJKM")] // eleven
    [InlineData("ABCDE-FGHJ0")] // zero is not in the alphabet
    [InlineData("ABCDE-FGHJO")] // nor the letter O
    [InlineData("ABCDE-FGHJ1")]
    [InlineData("ABCDE-FGHJI")]
    [InlineData("ABCDE-FGHJL")]
    [InlineData("ABCDE_FGHJK")]
    public void Normalize_RejectsWrongLengthsAndCharacters(string? typed) =>
        Assert.Null(SyncPairing.Normalize(typed));

    [Fact]
    public void NewCode_IsTwoGroupsOfFiveThatNormalize()
    {
        string code = SyncPairing.NewCode();

        Assert.Matches("^[A-Z2-9]{5}-[A-Z2-9]{5}$", code);
        Assert.Equal(code.Replace("-", string.Empty), SyncPairing.Normalize(code));
    }

    [Fact]
    public async Task OfferThenFetch_RoundTripsHoweverTheCodeIsTyped()
    {
        var server = new FakePairingServer();
        using var http = new HttpClient(server);

        await SyncPairing.OfferAsync(Server, "ABCDE-FGHJK", "the long sync code", http);

        Assert.Equal("the long sync code", await SyncPairing.FetchAsync(Server, "abcde fghjk", http));
    }

    [Fact]
    public async Task Offer_DoesNotShowTheServerTheCodeOrTheSecret()
    {
        var server = new FakePairingServer();
        using var http = new HttpClient(server);

        await SyncPairing.OfferAsync(Server, "ABCDE-FGHJK", "the long sync code", http);

        (string name, string body) = Assert.Single(server.Stored);
        Assert.DoesNotContain("ABCDEFGHJK", name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("the long sync code", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(body)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_WithAnotherCode_FindsNothing()
    {
        var server = new FakePairingServer();
        using var http = new HttpClient(server);
        await SyncPairing.OfferAsync(Server, "ABCDE-FGHJK", "the long sync code", http);

        await Assert.ThrowsAsync<PairingCodeException>(() => SyncPairing.FetchAsync(Server, "ABCDE-FGHJM", http));
    }

    [Fact]
    public async Task Fetch_WithAnotherCode_CannotOpenWhatWasLeftEvenIfTheServerHandsItOver()
    {
        var server = new FakePairingServer { AnswerAnyName = true };
        using var http = new HttpClient(server);
        await SyncPairing.OfferAsync(Server, "ABCDE-FGHJK", "the long sync code", http);

        await Assert.ThrowsAsync<PairingCodeException>(() => SyncPairing.FetchAsync(Server, "ABCDE-FGHJM", http));
    }

    [Fact]
    public async Task Fetch_WithACodeThatCannotBeOne_NeverAsksTheServer()
    {
        var server = new FakePairingServer();
        using var http = new HttpClient(server);

        await Assert.ThrowsAsync<PairingCodeException>(() => SyncPairing.FetchAsync(Server, "too short", http));
        Assert.Equal(0, server.Requests);
    }

    /// <summary>Stands in for the sync server's pairing endpoint, in memory: nothing here touches the network.</summary>
    private sealed class FakePairingServer : HttpMessageHandler
    {
        public Dictionary<string, string> Stored { get; } = [];
        public bool AnswerAnyName { get; init; }
        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            string name = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                Stored[name] = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (Stored.TryGetValue(name, out string? body) || (AnswerAnyName && (body = Stored.Values.FirstOrDefault()) is not null))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}

/// <summary>The longer code that hands a new device the key itself, so that it asks for no password.</summary>
public sealed class DeviceLinkTests
{
    private static readonly byte[] Key = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];

    [Fact]
    public void NewLinkCode_IsFourGroupsOfFive()
    {
        string code = SyncPairing.NewLinkCode();

        Assert.Matches("^[A-Z2-9]{5}(-[A-Z2-9]{5}){3}$", code);
    }

    [Fact]
    public void NormalizeLink_TakesWhatAPersonWouldType()
    {
        string code = SyncPairing.NewLinkCode();

        Assert.Equal(code.Replace("-", string.Empty), SyncPairing.NormalizeLink(" " + code.ToLowerInvariant().Replace('-', ' ') + " "));
    }

    [Fact]
    public void AShortCodeIsNotALink_AndALinkIsNotAShortCode()
    {
        Assert.Null(SyncPairing.NormalizeLink(SyncPairing.NewCode()));
        Assert.Null(SyncPairing.Normalize(SyncPairing.NewLinkCode()));
    }

    [Theory]
    [InlineData("esk_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData(null)]
    public void SealedLink_OpensWithItsCode(string? accountKey)
    {
        string code = SyncPairing.NewLinkCode();
        (string name, string sealedLink) = SyncPairing.SealLink(code, new SyncPairing.DeviceLink(accountKey, Key));

        SyncPairing.DeviceLink opened = SyncPairing.OpenLink(code, sealedLink);

        Assert.Equal(64, name.Length);
        Assert.Equal(accountKey, opened.AccountKey);
        Assert.Equal(Key, opened.DataKey);
    }

    [Fact]
    public void SealedLink_FitsWhatTheServerKeeps()
    {
        (_, string sealedLink) = SyncPairing.SealLink(SyncPairing.NewLinkCode(), new SyncPairing.DeviceLink("esk_" + new string('a', 64), Key));

        Assert.True(sealedLink.Length <= 512);
    }

    [Fact]
    public void SealedLink_DoesNotOpenWithAnotherCode()
    {
        (_, string sealedLink) = SyncPairing.SealLink(SyncPairing.NewLinkCode(), new SyncPairing.DeviceLink(null, Key));

        Assert.Throws<PairingCodeException>(() => SyncPairing.OpenLink(SyncPairing.NewLinkCode(), sealedLink));
    }

    [Fact]
    public void SealedLink_ChangedOnTheWay_DoesNotOpen()
    {
        string code = SyncPairing.NewLinkCode();
        (_, string sealedLink) = SyncPairing.SealLink(code, new SyncPairing.DeviceLink(null, Key));
        byte[] bytes = Convert.FromBase64String(sealedLink);
        bytes[^1] ^= 1;

        Assert.Throws<PairingCodeException>(() => SyncPairing.OpenLink(code, Convert.ToBase64String(bytes)));
    }

    [Fact]
    public void TheNameTheServerSees_SaysNothingAboutTheKey()
    {
        string code = SyncPairing.NewLinkCode();
        (string first, _) = SyncPairing.SealLink(code, new SyncPairing.DeviceLink(null, Key));
        (string again, _) = SyncPairing.SealLink(code, new SyncPairing.DeviceLink(null, Key));
        (string other, _) = SyncPairing.SealLink(SyncPairing.NewLinkCode(), new SyncPairing.DeviceLink(null, Key));

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
    }
}
