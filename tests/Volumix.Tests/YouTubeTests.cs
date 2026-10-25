using System.Diagnostics;
using Volumix.Services;

namespace Volumix.Tests;

public class YouTubeTests
{
    [Theory]
    [InlineData("Eminem - Not Afraid", "EminemVEVO", "Not Afraid", "Eminem")]
    [InlineData("Linkin Park - Numb (Official Music Video) [4K UPGRADE]", "Linkin Park", "Numb", "Linkin Park")]
    [InlineData("Скриптонит — Привычка | Премьера клипа", "Скриптонит", "Привычка", "Скриптонит")]
    [InlineData("Not Afraid", "Eminem - Topic", "Not Afraid", "Eminem")]
    [InlineData("Bohemian Rhapsody (Remastered 2011)", "QueenVEVO", "Bohemian Rhapsody (Remastered 2011)", "Queen")]
    [InlineData("Котик играет на пианино", null, "Котик играет на пианино", null)]
    public void ParsesVideoTitle(string title, string? channel, string song, string? artist) =>
        Assert.Equal((song, artist), YouTubeService.ParseTitle(title, channel));

    [Theory]
    [InlineData("Not Afraid", "Not Afraid", true)]
    [InlineData("Numb", "Numb (Live)", true)]
    [InlineData("Numb", "Numb / Encore", false)]
    [InlineData("Not Afraid", "Lose Yourself", false)]
    public void ComparesSongs(string a, string b, bool same) => Assert.Equal(same, YouTubeService.SameSong(a, b));

    [Theory]
    [InlineData("Eminem", "Eminem", true)]
    [InlineData("Eminem", "Eminem & Rihanna", true)]
    [InlineData("Eminem", "Linkin Park", false)]
    public void ComparesArtists(string a, string b, bool same) => Assert.Equal(same, YouTubeService.SameArtist(a, b));

    /// <summary>Официальная обложка есть в iTunes — она и должна прийти, квадратная и крупная.</summary>
    [SkippableFact]
    public async Task PrefersOfficialCover()
    {
        var cover = await YouTubeService.FindCoverAsync("Eminem - Not Afraid", "EminemVEVO", Array.Empty<byte>(), CancellationToken.None);
        Skip.If(cover == null, "iTunes недоступен");
        using var image = new System.Drawing.Bitmap(new System.IO.MemoryStream(cover!));
        Assert.Equal(image.Width, image.Height);
        Assert.True(image.Width >= 600, $"обложка {image.Width}×{image.Height}");
    }
}
