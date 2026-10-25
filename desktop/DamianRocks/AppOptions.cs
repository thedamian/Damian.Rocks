using System;
using System.Net;

namespace DamianRocks;

public sealed record AppOptions(Uri DisplayUri, bool IsDefaultServer, string? Key = null)
{
    public const string DefaultServer = "damian.rocks";

    public const string Usage =
        "DamianRocks.exe [--server <url>] [--key <presenter key>]\n\n" +
        "Connects to wss://damian.rocks/display by default.\n" +
        "--key is the server's DISPLAY_KEY; it lets this PC take audience questions\n" +
        "(it's remembered, so you only need to pass it once).\n\n" +
        "Examples:\n" +
        "  DamianRocks.exe --server localhost:3002\n" +
        "  DamianRocks.exe --server http://192.168.1.20:3002\n" +
        "  DamianRocks.exe --server wss://staging.damian.rocks/display\n" +
        "  DamianRocks.exe --key my-secret";

    /// <summary>Base http(s) URL of the server, used to resolve relative image paths.</summary>
    public Uri HttpBase => new UriBuilder(DisplayUri)
    {
        Scheme = DisplayUri.Scheme == "wss" ? "https" : "http",
        Path = "/",
        Query = "",
    }.Uri;

    /// <summary>Parses command line args. Returns null and sets <paramref name="error"/> on invalid input.</summary>
    public static AppOptions? Parse(string[] args, out string? error, out bool showHelp)
    {
        error = null;
        showHelp = false;
        string? server = null;
        string? key = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-h" or "--help" or "/?")
            {
                showHelp = true;
                return null;
            }
            if (arg is "--server" or "-s" or "--url")
            {
                if (i + 1 >= args.Length) { error = $"Missing value for {arg}."; return null; }
                server = args[++i];
            }
            else if (arg.StartsWith("--server=", StringComparison.OrdinalIgnoreCase) ||
                     arg.StartsWith("--url=", StringComparison.OrdinalIgnoreCase))
            {
                server = arg[(arg.IndexOf('=') + 1)..];
            }
            else if (arg is "--key" or "-k")
            {
                if (i + 1 >= args.Length) { error = $"Missing value for {arg}."; return null; }
                key = args[++i];
            }
            else if (arg.StartsWith("--key=", StringComparison.OrdinalIgnoreCase))
            {
                key = arg[(arg.IndexOf('=') + 1)..];
            }
            else if (!arg.StartsWith('-') && server is null)
            {
                server = arg;
            }
            else
            {
                error = $"Unknown argument: {arg}";
                return null;
            }
        }

        try
        {
            return new AppOptions(NormalizeServer(server ?? DefaultServer), server is null, string.IsNullOrWhiteSpace(key) ? null : key.Trim());
        }
        catch (Exception ex)
        {
            error = $"Invalid server URL \"{server}\": {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Accepts "damian.rocks", "localhost:3002", "http(s)://host", or "ws(s)://host/path"
    /// and returns the WebSocket URL of the display endpoint.
    /// </summary>
    public static Uri NormalizeServer(string input)
    {
        string s = input.Trim();
        if (s.Length == 0) throw new FormatException("URL is empty.");
        if (!s.Contains("://", StringComparison.Ordinal))
        {
            string host = s.Split('/', ':')[0];
            bool local = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host, out _);
            s = (local ? "ws://" : "wss://") + s;
        }

        var builder = new UriBuilder(s);
        bool defaultPort = builder.Uri.IsDefaultPort;
        builder.Scheme = builder.Scheme.ToLowerInvariant() switch
        {
            "http" or "ws" => "ws",
            "https" or "wss" => "wss",
            var other => throw new FormatException($"Unsupported scheme '{other}'."),
        };
        if (defaultPort) builder.Port = -1;
        if (builder.Path is "" or "/") builder.Path = "/display";
        return builder.Uri;
    }
}
