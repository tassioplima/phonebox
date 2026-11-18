using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhoneBox.Agent;

/// <summary>One ranked game: the RP it gave or took, the RP after it, when it was seen and the BR kills it added.</summary>
public sealed record ApexGame(int Rp, int Total, long At, int? Kills);

public sealed record ApexSession(bool Active, long? StartedAt, int? BaseRp, int? Rp, int? Kills, int? Wins, IReadOnlyList<ApexGame> Games);

public sealed record ApexNext(string Name, int Remaining, double Percent, string? Icon);

public sealed record ApexSnapshot(bool Configured, string? Error, long UpdatedAt,
    string? Rank, string? Tier, int? Division, int? Rp, string? RankIcon, int? PredatorPosition, ApexNext? Next,
    int? Level, string? Prestige, string? Legend, string? LegendIcon, int? BrKills, int? CareerKills, int? BrWins,
    ApexSession Session);

/// <summary>
/// Apex Legends ranked stats for the Games widgets. Reads the public overlay data of TAS.gg (Twitch Apex Stats) for the
/// Twitch channel set in Settings › General › Apex Legends, once a minute and only while an Apex widget is on a shown
/// page. It keeps its own ranked session on this computer: the session starts with the first RP change of the day
/// (the RP before it is the baseline), every RP change after that is one game, and the next day waits for the first
/// ranked game again. The phone's Start session button restarts it on demand. Nothing but the channel id is sent.
/// </summary>
public sealed class ApexMonitor(ConfigStore config, ILogger<ApexMonitor> log) : BackgroundService
{
    internal const string BaseUrl = "https://api.tas.gg/overlays/";
    private const int MaxGames = 20;

    private volatile ApexSnapshot _snap = Empty(false);
    private readonly SemaphoreSlim _poll = new(1, 1);
    private readonly Lock _stateGate = new();
    private State _state = new();
    private bool _loaded;
    private CancellationTokenSource _wake = new();

    /// <summary>For tests: a fake handler instead of the network.</summary>
    internal HttpMessageHandler? HttpHandler { get; init; }
    /// <summary>Where the session is kept between restarts. Tests point it at a file of their own.</summary>
    internal string StatePath { get; init; } = Path.Combine(AppPaths.LocalDir, "apex-session.json");
    /// <summary>For tests: the clock.</summary>
    internal Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;

    private HttpClient? _http;
    private HttpClient Http => _http ??= NewHttp(HttpHandler ?? new SocketsHttpHandler());

    private static HttpClient NewHttp(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneBox/" + AppPaths.Version);
        return http;
    }

    public ApexSnapshot Snapshot() => _snap;

    /// <summary>The TAS.gg channel id: the Twitch user id, digits only.</summary>
    internal static string? Channel(AgentConfig c) =>
        c.Apex.TasChannel is { Length: > 0 } s && s.All(char.IsAsciiDigit) ? s : null;

    private static bool Wanted(AgentConfig c) =>
        c.Widgets(enabledPagesOnly: true).Any(w => w.Type.StartsWith("apex-", StringComparison.Ordinal));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var c = config.Get();
            if (Channel(c) is null) _snap = Empty(false) with { Error = null };
            else if (Wanted(c)) await PollAsync(ct);
            CancellationTokenSource wake;
            lock (_stateGate) wake = _wake;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, wake.Token);
            try { await Task.Delay(TimeSpan.FromSeconds(60), linked.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
    }

    /// <summary>Start session: the session starts now, at the current RP.</summary>
    public string? StartSession()
    {
        if (Channel(config.Get()) is null) return "set the TAS.gg channel in Settings › General › Apex Legends";
        lock (_stateGate)
        {
            Load();
            _state.StartRequested = true;
            Save();
            var old = _wake;
            _wake = new CancellationTokenSource();
            old.Cancel();
        }
        log.LogInformation("apex: session restarted from the phone");
        return null;
    }

    internal async Task PollAsync(CancellationToken ct)
    {
        if (!await _poll.WaitAsync(0, ct)) return;
        try
        {
            var channel = Channel(config.Get());
            if (channel is null) return;
            var card = await Get(channel, "edge-card", ct);
            var level = await Get(channel, "edge-level", ct);
            var trackers = await Get(channel, "edge-trackers", ct);
            if (card?["status"]?.GetValue<string>() is { } st && st != "ok")
                throw new Exception("TAS.gg has no stats for this channel yet (link the game account on tas.gg)");
            Apply(card, level, trackers);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var msg = ex is HttpRequestException or TaskCanceledException ? "TAS.gg did not answer" : ex.Message;
            if (_snap.Error != msg) log.LogInformation("apex: {Msg}", msg);
            _snap = _snap with { Configured = true, Error = msg };
        }
        finally { _poll.Release(); }
    }

    private async Task<JsonNode?> Get(string channel, string design, CancellationToken ct)
    {
        using var r = await Http.GetAsync($"{BaseUrl}{channel}/default/{design}?lang=en&initial", ct);
        r.EnsureSuccessStatusCode();
        return JsonNode.Parse(await r.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Folds one reading into the session and publishes the snapshot (separate from the network for tests).</summary>
    internal void Apply(JsonNode? card, JsonNode? level, JsonNode? trackers)
    {
        var rank = card?["rank"];
        var rp = Int(rank?["score"]);
        var tv = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in trackers?["trackers"]?.AsArray() ?? [])
            if (t?["name"]?.GetValue<string>() is { } name) tv[name] = Int(t["value"]);
        var kills = tv.GetValueOrDefault("BR Kills");
        var wins = tv.GetValueOrDefault("BR Wins");
        var now = Now();
        ApexSession session;
        lock (_stateGate)
        {
            Load();
            session = Advance(_state, now, rp, kills, wins);
            Save();
        }
        var next = card?["next"] is { } n && n["name"]?.GetValue<string>() is { } nn
            ? new ApexNext(nn, Int(n["remaining"]) ?? 0, Dbl(n["percent"]) ?? 0, Str(n["icon"])) : null;
        _snap = new ApexSnapshot(true, null, now.ToUnixTimeMilliseconds(),
            Str(rank?["name"]), Str(rank?["tier"]), Int(rank?["division"]), rp, Str(rank?["icon"]), Int(rank?["position"]), next,
            Int(level?["level"]?["level"]), Str(level?["level"]?["prestige"]), Str(trackers?["legend"]?["name"]) ?? Str(level?["legend"]?["name"]),
            Str(trackers?["legend"]?["icon"]) ?? Str(level?["legend"]?["icon"]),
            kills, tv.GetValueOrDefault("Career Kills"), wins, session);
    }

    /// <summary>The session rules (see the class summary). Pure apart from the state it updates.</summary>
    internal static ApexSession Advance(State s, DateTimeOffset now, int? rp, int? kills, int? wins)
    {
        var today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        ApexGame? game = null;
        if (rp is not null && s.LastRp is not null && rp != s.LastRp)
            game = new ApexGame(rp.Value - s.LastRp.Value, rp.Value, now.ToUnixTimeMilliseconds(),
                kills is not null && s.LastKills is not null ? kills - s.LastKills : null);

        if (s.StartRequested && rp is not null)
        {
            s.StartRequested = false;
            s.Day = today; s.Active = true; s.StartedAt = now.ToUnixTimeMilliseconds();
            s.BaseRp = rp; s.BaseKills = kills; s.BaseWins = wins; s.Games = [];
            game = null;
        }
        else if (s.Day != today)
        {
            s.Day = today; s.Active = false; s.Games = [];
        }
        else if (!s.Active && game is not null)
        {
            // the first ranked game of the day: the session starts with the RP before it
            s.Active = true; s.StartedAt = now.ToUnixTimeMilliseconds();
            s.BaseRp = s.LastRp; s.BaseKills = s.LastKills; s.BaseWins = s.LastWins; s.Games = [];
        }
        if (s.Active && game is not null) s.Games = [.. s.Games.TakeLast(MaxGames - 1), game];
        if (rp is not null) s.LastRp = rp;
        if (kills is not null) s.LastKills = kills;
        if (wins is not null) s.LastWins = wins;

        return s.Active
            ? new ApexSession(true, s.StartedAt, s.BaseRp, Diff(rp ?? s.LastRp, s.BaseRp), Diff(kills ?? s.LastKills, s.BaseKills),
                Diff(wins ?? s.LastWins, s.BaseWins), s.Games)
            : new ApexSession(false, null, null, null, null, null, []);
    }

    private static int? Diff(int? a, int? b) => a is null || b is null ? null : a - b;

    private void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try { if (File.Exists(StatePath)) _state = JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new(); }
        catch (Exception ex) { log.LogWarning("apex: session file unreadable, starting over ({Msg})", ex.Message); _state = new(); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state));
            File.Move(tmp, StatePath, overwrite: true);
        }
        catch (Exception ex) { log.LogWarning("apex: could not save the session ({Msg})", ex.Message); }
    }

    private static ApexSnapshot Empty(bool configured) => new(configured, null, 0, null, null, null, null, null, null, null,
        null, null, null, null, null, null, null, new ApexSession(false, null, null, null, null, null, []));

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static double? Dbl(JsonNode? n) => n is JsonValue v
        ? v.TryGetValue<double>(out var d) ? d : double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : null
        : null;

    /// <summary>Numbers arrive as numbers or as text with thousands separators ("13,475").</summary>
    private static int? Int(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)l;
        if (v.TryGetValue<double>(out var d)) return (int)Math.Round(d);
        var digits = new string((v.ToString() ?? "").Where(c => char.IsAsciiDigit(c) || c == '-').ToArray());
        return int.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out i) ? i : null;
    }

    internal sealed class State
    {
        public string? Day { get; set; }
        public bool Active { get; set; }
        public bool StartRequested { get; set; }
        public long? StartedAt { get; set; }
        public int? BaseRp { get; set; }
        public int? BaseKills { get; set; }
        public int? BaseWins { get; set; }
        public int? LastRp { get; set; }
        public int? LastKills { get; set; }
        public int? LastWins { get; set; }
        public List<ApexGame> Games { get; set; } = [];
    }
}
