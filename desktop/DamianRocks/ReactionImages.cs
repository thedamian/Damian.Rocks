using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DamianRocks;

/// <summary>
/// Resolves reactions to images: bundled Noto Emoji artwork for emojis (WPF can't draw color
/// emoji fonts), or images downloaded from the server for custom reactions.
/// </summary>
public sealed class ReactionImages
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Uri _httpBase;
    private readonly Dictionary<string, ImageSource?> _emojiCache = new();
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _remoteCache = new();

    public ReactionImages(Uri httpBase) => _httpBase = httpBase;

    public static readonly string[] BundledEmojis = { "😁", "😂", "👍🏽", "👎🏽", "💩", "❓", "🖐🏽" };
    /// <summary>The raised hand used for the questions list (not a floating reaction).</summary>
    public const string HandEmoji = "🖐🏽";

    public Task<ImageSource?> GetAsync(ReactionMessage reaction)
    {
        if (!string.IsNullOrEmpty(reaction.Image) && Uri.TryCreate(_httpBase, reaction.Image, out var url) &&
            (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps))
        {
            return _remoteCache.GetOrAdd(url.AbsoluteUri, _ => DownloadAsync(url));
        }
        return Task.FromResult(reaction.Emoji is null ? null : GetEmoji(reaction.Emoji));
    }

    /// <summary>Must be called on the UI thread.</summary>
    public ImageSource? GetEmoji(string emoji)
    {
        if (_emojiCache.TryGetValue(emoji, out var cached)) return cached;
        ImageSource? image = null;
        try
        {
            var uri = new Uri($"pack://application:,,,/Assets/Emoji/{NotoFileName(emoji)}", UriKind.Absolute);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            image = bmp;
        }
        catch (IOException)
        {
            // not bundled; caller falls back to text
        }
        _emojiCache[emoji] = image;
        return image;
    }

    /// <summary>"👍🏽" → "emoji_u1f44d_1f3fd.png" (Noto naming; variation selectors dropped).</summary>
    public static string NotoFileName(string emoji)
    {
        var codepoints = new List<string>();
        var e = emoji.EnumerateRunes();
        foreach (var rune in e)
        {
            if (rune.Value == 0xFE0F) continue;
            codepoints.Add(rune.Value.ToString("x4"));
        }
        return $"emoji_u{string.Join('_', codepoints)}.png";
    }

    private async Task<ImageSource?> DownloadAsync(Uri url)
    {
        try
        {
            byte[] bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 512;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            _remoteCache.TryRemove(url.AbsoluteUri, out _); // allow a retry next time
            return null;
        }
    }
}
