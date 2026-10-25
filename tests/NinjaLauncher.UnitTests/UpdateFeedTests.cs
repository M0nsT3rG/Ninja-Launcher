using System.Net;
using System.Text;
using NinjaLauncher.Infrastructure.Updates;
using Velopack.Logging;

namespace NinjaLauncher.UnitTests;

public sealed class UpdateFeedTests
{
    private const string Feed = """{"Assets":[{"PackageId":"NinjaLauncher","Version":"0.2.0","Type":"Full","FileName":"NinjaLauncher-0.2.0-win-x64-full.nupkg","SHA1":"AA","SHA256":"BB","Size":10}]}""";
    private static readonly (string Private, string Public) Keys = FeedSignature.GenerateKeyPair();

    private sealed class FakeHandler(Dictionary<string, string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(responses.TryGetValue(request.RequestUri!.AbsoluteUri, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body), RequestMessage = request }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
    }

    private static SignedFeedSource Source(string feed, string signature, string channelInUrl = "win-x64", string publicKey = "") =>
        new("https://example.test/u", publicKey.Length > 0 ? publicKey : Keys.Public, new HttpClient(new FakeHandler(new()
        {
            [$"https://example.test/u/releases.{channelInUrl}.json"] = feed,
            [$"https://example.test/u/releases.{channelInUrl}.json.sig"] = signature,
        })));

    [Fact]
    public async Task Valid_signature_yields_the_feed()
    {
        var sig = FeedSignature.Sign(Encoding.UTF8.GetBytes(Feed), "win-x64", Keys.Private);
        var feed = await Source(Feed, sig).GetReleaseFeed(NullVelopackLogger.Instance, "NinjaLauncher", "win-x64");
        Assert.Equal("0.2.0", Assert.Single(feed.Assets).Version.ToString());
    }

    [Fact]
    public async Task Tampered_feed_is_refused()
    {
        var sig = FeedSignature.Sign(Encoding.UTF8.GetBytes(Feed), "win-x64", Keys.Private);
        await Assert.ThrowsAsync<UpdateSignatureException>(() =>
            Source(Feed.Replace("0.2.0", "9.9.9"), sig).GetReleaseFeed(NullVelopackLogger.Instance, "NinjaLauncher", "win-x64"));
    }

    [Fact]
    public async Task Feed_signed_for_another_channel_is_refused()
    {
        var sig = FeedSignature.Sign(Encoding.UTF8.GetBytes(Feed), "linux-x64", Keys.Private);
        await Assert.ThrowsAsync<UpdateSignatureException>(() => Source(Feed, sig).GetReleaseFeed(NullVelopackLogger.Instance, "NinjaLauncher", "win-x64"));
    }

    [Fact]
    public async Task Signature_from_a_different_key_is_refused()
    {
        var other = FeedSignature.GenerateKeyPair();
        var sig = FeedSignature.Sign(Encoding.UTF8.GetBytes(Feed), "win-x64", other.PrivatePem);
        await Assert.ThrowsAsync<UpdateSignatureException>(() => Source(Feed, sig).GetReleaseFeed(NullVelopackLogger.Instance, "NinjaLauncher", "win-x64"));
    }

    [Theory]
    [InlineData("not base64!!")]
    [InlineData("")]
    public void Garbage_signatures_do_not_verify(string signature) =>
        Assert.False(FeedSignature.Verify(Encoding.UTF8.GetBytes(Feed), "win-x64", signature, Keys.Public));

    [Fact]
    public async Task Missing_signature_file_fails_closed() =>
        await Assert.ThrowsAsync<HttpRequestException>(() => new SignedFeedSource("https://example.test/u", Keys.Public,
            new HttpClient(new FakeHandler(new() { ["https://example.test/u/releases.win-x64.json"] = Feed })))
            .GetReleaseFeed(NullVelopackLogger.Instance, "NinjaLauncher", "win-x64"));

    [Fact]
    public async Task Non_https_feed_is_refused() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SignedFeedSource("http://example.test/u", Keys.Public, new HttpClient())
            .GetReleaseFeed(NullVelopackLogger.Instance, "NinjaLauncher", "win-x64"));

    [Theory]
    [InlineData("releases.win-x64.json", "win-x64")]
    [InlineData("/tmp/out/releases.osx-arm64.json", "osx-arm64")]
    public void Channel_is_read_from_feed_file_name(string file, string channel) => Assert.Equal(channel, FeedSignature.ChannelFromFileName(file));

    [Theory]
    [InlineData("releases..json")]
    [InlineData("feed.json")]
    public void Bad_feed_file_names_are_rejected(string file) => Assert.Throws<ArgumentException>(() => FeedSignature.ChannelFromFileName(file));
}
