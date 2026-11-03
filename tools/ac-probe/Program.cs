// Logs in to an AzerothCore server through WowVR.Net, as the game does, and prints the
// creatures the server shows around the character and how they move.
//
//   dotnet run --project tools/ac-probe -- [--host=192.168.15.17] [--char=Name] [--seconds=30] [--moves] [--walk]
//
// The account comes from WOWVR_AC_USER and WOWVR_AC_PASS, or --user= and --pass=.
// --walk sends the character 60 yards north and back, to see what the server shows on the way.

using System.Diagnostics;
using System.Numerics;
using WowVR.Net;

var opts = args.Where(a => a.StartsWith("--")).Select(a => a[2..].Split('=', 2))
    .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");
var host = opts.GetValueOrDefault("host") ?? "192.168.15.17";
var user = opts.GetValueOrDefault("user") ?? Environment.GetEnvironmentVariable("WOWVR_AC_USER") ?? "";
var pass = opts.GetValueOrDefault("pass") ?? Environment.GetEnvironmentVariable("WOWVR_AC_PASS") ?? "";
var seconds = int.Parse(opts.GetValueOrDefault("seconds") ?? "30");
var printMoves = opts.ContainsKey("moves");
var walk = opts.ContainsKey("walk");
if (user == "" || pass == "")
{
    Console.Error.WriteLine("Set WOWVR_AC_USER and WOWVR_AC_PASS, or pass --user= and --pass=.");
    return 2;
}

var clock = Stopwatch.StartNew();
void Log(string s) => Console.WriteLine($"[{clock.Elapsed.TotalSeconds,6:F2}] {s}");

using var session = await RealmSession.OpenAsync(host, user, pass, opts.GetValueOrDefault("char"), Log);
var start = session.StartPosition;
Log($"in the world: map {session.MapId} at ({start.X:F1}, {start.Y:F1}, {start.Z:F1}), server time {session.ServerTimeOfDay:F2} h");

var units = new Dictionary<ulong, (UnitInfo Info, MovePath Path, double Since)>();
int appeared = 0, moved = 0, gone = 0;
var me = start;
var lastSent = 0.0;
while (clock.Elapsed.TotalSeconds < seconds)
{
    while (session.Events.TryDequeue(out var e))
    {
        var now = clock.Elapsed.TotalSeconds;
        switch (e)
        {
            case UnitAppeared a:
                appeared++;
                units[a.Unit.Guid] = (a.Unit, a.Unit.Path, now - a.Unit.PathElapsed);
                break;
            case UnitMoved m when units.TryGetValue(m.Guid, out var u):
                moved++;
                units[m.Guid] = (u.Info, m.Path, now - m.PathElapsed);
                if (printMoves)
                {
                    Log($"  {u.Info.Name} [{m.Guid & 0xFFFFFF}] {(m.Path.Points.Count == 1 ? "stops" : $"goes {m.Path.Length:F1} yd at {m.Path.Speed:F1} yd/s through {m.Path.Points.Count} points")}");
                }

                break;
            case UnitGone g:
                gone++;
                units.Remove(g.Guid);
                break;
            case SessionClosed c:
                Log($"closed: {c.Reason}");
                return 1;
        }
    }

    if (walk && clock.Elapsed.TotalSeconds - lastSent > 0.5)
    {
        // Out and back at walking pace over the run.
        lastSent = clock.Elapsed.TotalSeconds;
        var t = lastSent / seconds;
        me = start + new Vector3((float)(60 * (1 - Math.Abs(2 * t - 1))), 0, 0);
        session.SendPosition(me, 0);
    }

    await Task.Delay(50);
}

Log($"{appeared} appeared, {moved} moves, {gone} gone; {units.Count} in view now:");
var nowSeconds = clock.Elapsed.TotalSeconds;
foreach (var (info, path, since) in units.Values.OrderBy(u => Vector3.Distance(u.Path.At(nowSeconds - u.Since).Position, me)))
{
    var (position, _, moving) = path.At(nowSeconds - since);
    Log($"  {Vector3.Distance(position, me),6:F1} yd  {info.Name,-30} entry {info.Entry,6} display {info.DisplayId,6} scale {info.Scale:F2} items [{string.Join(", ", info.Items)}]  ({position.X:F1}, {position.Y:F1}, {position.Z:F1}){(moving ? " moving" : "")}");
}

return 0;
