using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class ApexMonitorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-apex-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 11, 18, 20, 0, 0, TimeSpan.Zero);

    public ApexMonitorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private ApexMonitor Monitor(string channel = "134895815")
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, $$"""{ "apex": { "tasChannel": "{{channel}}" } }""");
        return new ApexMonitor(new ConfigStore(path), NullLogger<ApexMonitor>.Instance)
        {
            StatePath = Path.Combine(_dir, "apex-session.json"),
            Now = () => _now,
        };
    }

    private static JsonNode Card(int rp) => JsonNode.Parse($$"""
        { "status": "ok", "rank": { "tier": "RANK_PLATINUM", "division": 4, "name": "Platinum IV", "icon": "https://assets.tas.gg/b.png", "score": {{rp}}, "position": null },
          "next": { "kind": "rank", "name": "Diamond", "icon": "https://assets.tas.gg/d.png", "remaining": {{12000 - rp}}, "percent": 13.3 } }
        """)!;
    private static JsonNode Level() => JsonNode.Parse("""{ "legend": { "name": "Octane" }, "level": { "level": 79, "prestige": "3rd" } }""")!;
    // TAS.gg sends tracker values as numbers here and as text with separators in other overlays
    private static JsonNode Trackers(int kills, int wins) => JsonNode.Parse($$"""
        { "legend": { "name": "Octane", "icon": "https://assets.tas.gg/o.png" },
          "trackers": [ { "name": "Career Kills", "value": "43,459" }, { "name": "BR Kills", "value": {{kills}} }, { "name": "BR Wins", "value": {{wins}} } ] }
        """)!;

    [Fact]
    public void SessionStartsWithTheFirstRankedGameOfTheDay()
    {
        var m = Monitor();
        m.Apply(Card(8967), Level(), Trackers(13477, 826));
        Assert.False(m.Snapshot().Session.Active);

        _now = _now.AddMinutes(20);
        m.Apply(Card(9120), Level(), Trackers(13480, 826));
        var s = m.Snapshot();
        Assert.True(s.Session.Active);
        Assert.Equal(8967, s.Session.BaseRp);
        Assert.Equal(153, s.Session.Rp);
        Assert.Equal(3, s.Session.Kills);
        var g = Assert.Single(s.Session.Games);
        Assert.Equal(153, g.Rp);
        Assert.Equal(3, g.Kills);
        Assert.Equal(43459, s.CareerKills);
        Assert.Equal("Platinum IV", s.Rank);
        Assert.Equal(2880, s.Next!.Remaining);
    }

    [Fact]
    public void EveryRpChangeIsOneGameAndUnchangedReadingsAddNone()
    {
        var m = Monitor();
        m.Apply(Card(9000), Level(), Trackers(100, 10));
        m.Apply(Card(9050), Level(), Trackers(102, 10));
        m.Apply(Card(9050), Level(), Trackers(104, 10)); // pubs: kills but no RP change
        m.Apply(Card(9020), Level(), Trackers(105, 10));
        var s = m.Snapshot().Session;
        Assert.Equal([50, -30], s.Games.Select(x => x.Rp));
        Assert.Equal(1, s.Games[1].Kills); // kills since the reading before the RP change, not the pubs before it
        Assert.Equal(20, s.Rp);
    }

    [Fact]
    public void NextDayWaitsForTheFirstRankedGameAgain()
    {
        var m = Monitor();
        m.Apply(Card(9000), Level(), Trackers(100, 10));
        m.Apply(Card(9100), Level(), Trackers(103, 10));
        Assert.True(m.Snapshot().Session.Active);

        _now = _now.AddDays(1);
        m.Apply(Card(9100), Level(), Trackers(103, 10));
        Assert.False(m.Snapshot().Session.Active);
        Assert.Empty(m.Snapshot().Session.Games);

        m.Apply(Card(9180), Level(), Trackers(106, 11));
        var s = m.Snapshot().Session;
        Assert.True(s.Active);
        Assert.Equal(9100, s.BaseRp);
        Assert.Equal(1, s.Wins);
    }

    [Fact]
    public void StartSessionRestartsAtTheCurrentRpAndSurvivesARestart()
    {
        var m = Monitor();
        m.Apply(Card(9000), Level(), Trackers(100, 10));
        m.Apply(Card(9100), Level(), Trackers(103, 10));
        Assert.Null(m.StartSession());
        m.Apply(Card(9100), Level(), Trackers(103, 10));
        var s = m.Snapshot().Session;
        Assert.True(s.Active);
        Assert.Equal(9100, s.BaseRp);
        Assert.Equal(0, s.Rp);
        Assert.Empty(s.Games);

        var again = Monitor(); // a new agent process reads the same session file
        again.Apply(Card(9150), Level(), Trackers(104, 10));
        Assert.Equal(9100, again.Snapshot().Session.BaseRp);
        Assert.Single(again.Snapshot().Session.Games);
    }

    [Theory]
    [InlineData("134895815", "134895815")]
    [InlineData(" 1348-95815 ", "134895815")]
    [InlineData("", null)]
    [InlineData("abc", null)]
    public void ChannelIsDigitsOnly(string input, string? expected)
    {
        var path = Path.Combine(_dir, "c.json");
        File.WriteAllText(path, $$"""{ "apex": { "tasChannel": "{{input}}" } }""");
        Assert.Equal(expected, ApexMonitor.Channel(new ConfigStore(path).Get()));
    }

    [Fact]
    public void StartSessionNeedsAChannel()
    {
        Assert.NotNull(Monitor("").StartSession());
    }
}

public sealed class CpuVoltPowerTests
{
    [Fact]
    public void ReadsTheCpuVoltageAndPackagePowerFromLibreHardwareMonitor()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            { "Text": "Sensor", "Children": [ { "Text": "PC", "Children": [
              { "Text": "MS-7C95", "HardwareId": "/motherboard", "Children": [ { "Text": "Nuvoton", "Children": [
                { "Text": "Voltages", "Children": [ { "Text": "Vcore", "Type": "Voltage", "Value": "0,982 V" } ] } ] } ] },
              { "Text": "AMD Ryzen 9 5900X", "HardwareId": "/amdcpu/0", "Children": [
                { "Text": "Voltages", "Children": [ { "Text": "Core (SVI2 TFN)", "Type": "Voltage", "Value": "1,394 V" } ] },
                { "Text": "Powers", "Children": [ { "Text": "Package", "Type": "Power", "Value": "107,1 W" } ] } ] } ] } ] }
            """);
        var (volt, power) = SystemMonitor.FindCpuVoltPower(doc.RootElement);
        Assert.Equal(1.394, volt);
        Assert.Equal(107.1, power);
    }

    [Fact]
    public void FallsBackToTheBoardVcoreWhenTheCpuHasNoVoltage()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            { "Text": "Sensor", "Children": [ { "Text": "Nuvoton", "Children": [ { "Text": "Vcore", "Type": "Voltage", "Value": "1.250 V" } ] },
              { "Text": "Intel Core i7", "HardwareId": "/intelcpu/0", "Children": [ { "Text": "CPU Package", "Type": "Power", "Value": "65.0 W" } ] } ] }
            """);
        Assert.Equal((1.25, 65.0), SystemMonitor.FindCpuVoltPower(doc.RootElement));
    }
}

public sealed class FanTests
{
    [Fact]
    public void PairsEachFanWithItsControlAndSkipsEmptyHeaders()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            { "Text": "Sensor", "Children": [ { "Text": "PC", "Children": [
              { "Text": "MS-7C95", "HardwareId": "/motherboard", "Children": [
                { "Text": "Nuvoton NCT6687D", "HardwareId": "/lpc/nct6687d/0", "Children": [
                  { "Text": "Fans", "Children": [
                    { "Text": "CPU Fan", "Type": "Fan", "Value": "1295 RPM", "SensorId": "/lpc/nct6687d/0/fan/0" },
                    { "Text": "System Fan #4", "Type": "Fan", "Value": "0 RPM", "SensorId": "/lpc/nct6687d/0/fan/5" } ] },
                  { "Text": "Controls", "Children": [
                    { "Text": "CPU Fan", "Type": "Control", "Value": "61,0 %", "SensorId": "/lpc/nct6687d/0/control/0" },
                    { "Text": "System Fan #4", "Type": "Control", "Value": "100,0 %", "SensorId": "/lpc/nct6687d/0/control/5" } ] } ] } ] },
              { "Text": "NVIDIA GeForce RTX 4070 SUPER", "HardwareId": "/gpu-nvidia/0", "Children": [
                { "Text": "Fans", "Children": [ { "Text": "GPU Fan 1", "Type": "Fan", "Value": "0 RPM", "SensorId": "/gpu-nvidia/0/fan/1" } ] } ] } ] } ] }
            """);
        var fans = SystemMonitor.FindFans(doc.RootElement);
        Assert.Equal(2, fans.Count);
        Assert.Equal(new FanInfo("CPU Fan", "Nuvoton NCT6687D", 1295, 61), fans[0]);
        Assert.Equal(new FanInfo("GPU Fan 1", "NVIDIA GeForce RTX 4070 SUPER", 0, null), fans[1]);
    }
}
