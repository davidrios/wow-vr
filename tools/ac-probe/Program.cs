// Logs in to an AzerothCore server through WowVR.Net, as the game does, and prints the
// creatures the server shows around the character and how they move.
//
//   dotnet run --project tools/ac-probe -- [--host=192.168.15.17] [--char=Name] [--seconds=30] [--moves] [--walk]
//
// The account comes from WOWVR_AC_USER and WOWVR_AC_PASS, or --user= and --pass=.
// --say="line;line" sends chat lines or commands 1.5 seconds apart, and prints what comes back.
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

// --list prints the realms and each character's look and gear, without entering the world.
if (opts.ContainsKey("list"))
{
    var (auth, realms) = await AuthClient.LoginAsync(host, 3724, user, pass);
    foreach (var realm in realms)
    {
        Log($"realm #{realm.Id} {realm.Name} ({realm.TypeName}) at {realm.Address}, population {realm.Population}, {realm.Characters} characters{(realm.Offline ? ", offline" : "")}");
    }

    using var listing = await RealmSession.ConnectAsync(host, auth, realms[0], Log);
    foreach (var c in await listing.ListCharactersAsync())
    {
        Log($"  {c.Name}: race {c.Race} class {c.Class} sex {c.Sex}, skin {c.Skin} face {c.Face} hair {c.HairStyle}/{c.HairColor} facial {c.FacialHair}, level {c.Level}, flags 0x{c.Flags:X}");
        Log($"    gear: {string.Join(" ", c.Equipment.Select((e, slot) => e.DisplayId == 0 ? null : $"{slot}:{e.DisplayId}/{e.InventoryType}").Where(s => s is not null))}");
    }

    return 0;
}

using var session = await RealmSession.OpenAsync(host, user, pass, opts.GetValueOrDefault("char"), Log);
var start = session.StartPosition;
Log($"in the world: map {session.MapId} at ({start.X:F1}, {start.Y:F1}, {start.Z:F1}), server time {session.ServerTimeOfDay:F2} h");

var units = new Dictionary<ulong, (UnitInfo Info, MovePath Path, double Since)>();
int appeared = 0, moved = 0, gone = 0;
var me = start;
var lastSent = 0.0;
var says = new Queue<string>((opts.GetValueOrDefault("say") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
var nextSay = 3.0;
var talk = opts.GetValueOrDefault("talk");
var talkStep = 0;
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
                if (talk is not null && talkStep == 0 && a.Unit.Name.Contains(talk, StringComparison.OrdinalIgnoreCase))
                {
                    talkStep = 1;
                    var at = a.Unit.Path.At(a.Unit.PathElapsed).Position;
                    session.Say(FormattableString.Invariant($".go xyz {at.X + 1.5f:F1} {at.Y:F1} {at.Z + 0.5f:F1}"));
                    var npc = a.Unit;
                    _ = Task.Delay(3000).ContinueWith(_ =>
                    {
                        Log($"talking to {npc.Name}");
                        session.Interact(npc.Stats! with { NpcFlags = NpcFlagMask.QuestGiver });
                    });
                }

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
            case ChatReceived chat:
                Log($"chat {chat.Line.Kind} {chat.Line.Sender}: {chat.Line.Text}");
                break;
            case ScreenMessage message:
                Log($"screen: {message.Text}");
                break;
            case SelfChanged self:
                Log($"self: health {self.Stats.Unit.Health}/{self.Stats.Unit.MaxHealth}, {self.Stats.Unit.PowerType} {self.Stats.Unit.Power}/{self.Stats.Unit.MaxPower}, level {self.Stats.Unit.Level}, money {RealmSession.Money(self.Stats.Money)}, flags 0x{self.Stats.PlayerFlags:X}{(self.Stats.IsGhost ? " ghost" : "")}");
                break;
            case Teleported t:
                me = t.Position;
                Log($"teleported to ({t.Position.X:F1}, {t.Position.Y:F1}, {t.Position.Z:F1})");
                break;
            case WorldChanged w:
                me = w.Position;
                Log($"new world: map {w.Map} at ({w.Position.X:F1}, {w.Position.Y:F1}, {w.Position.Z:F1})");
                session.AckWorldPort();
                break;
            case GossipShown g:
                Log($"gossip from {g.Npc:X}: '{g.Text}', options [{string.Join("; ", g.Options.Select(o => o.Text))}], quests [{string.Join("; ", g.Quests.Select(q => $"{q.Id} {q.Title} icon {q.Icon}"))}]");
                if (talkStep == 1 && g.Quests.Count > 0)
                {
                    talkStep = 2;
                    session.ChooseGossipQuest(g.Npc, g.Quests[0]);
                    Log($"chose quest {g.Quests[0].Id} (icon {g.Quests[0].Icon})");
                }

                break;
            case QuestShown q:
                Log($"quest {q.QuestId} {q.Step}: '{q.Title}', completable {q.Completable}, {q.Choices.Count} choices");
                if (q.Step == QuestStep.OfferReward)
                {
                    session.ChooseQuestReward(q.Npc, q.QuestId, 0);
                }
                else if (q.Step == QuestStep.RequestItems && q.Completable)
                {
                    session.RequestQuestReward(q.Npc, q.QuestId);
                }

                break;
            case InventoryChanged inv:
                Log($"inventory: {inv.Items.Count} items, bags {string.Join(",", inv.Bags.Select(b => $"{b.Bag}:{b.Slots}"))}");
                break;
            case GameObjectAppeared go when printMoves:
                Log($"  object {go.Object.Name} entry {go.Object.Entry} type {go.Object.Type} display {go.Object.DisplayId}");
                break;
            case SessionClosed c:
                Log($"closed: {c.Reason}");
                return 1;
        }
    }

    if (says.Count > 0 && clock.Elapsed.TotalSeconds > nextSay)
    {
        nextSay = clock.Elapsed.TotalSeconds + 1.5;
        var line = says.Dequeue();
        Log($"say: {line}");
        session.Say(line);
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

// --relog logs out to the character list and enters again on the same connection.
if (opts.ContainsKey("relog"))
{
    var answer = await session.RequestLogoutAsync();
    Log(answer.Refused is { } refused ? $"logout refused: {refused}" : $"logging out, {(answer.Instant ? "at once" : "after the server's wait")}");
    if (answer.Refused is null)
    {
        await session.WaitForLogoutAsync();
        var characters = await session.ListCharactersAsync();
        Log($"back at the character list: {string.Join(", ", characters.Select(c => c.Name))}");
        await session.EnterWorldAsync(characters.First(c => c.Name == session.Character.Name));
        Log($"in the world again at ({session.StartPosition.X:F1}, {session.StartPosition.Y:F1}, {session.StartPosition.Z:F1})");
        await Task.Delay(3000);
        Log($"{session.Events.Count} events since entering again");
    }

    return 0;
}

Log($"{appeared} appeared, {moved} moves, {gone} gone; {units.Count} in view now:");
var nowSeconds = clock.Elapsed.TotalSeconds;
foreach (var (info, path, since) in units.Values.OrderBy(u => Vector3.Distance(u.Path.At(nowSeconds - u.Since).Position, me)))
{
    var (position, _, moving) = path.At(nowSeconds - since);
    Log($"  {Vector3.Distance(position, me),6:F1} yd  {info.Name,-30} entry {info.Entry,6} display {info.DisplayId,6} scale {info.Scale:F2} items [{string.Join(", ", info.Items)}]  ({position.X:F1}, {position.Y:F1}, {position.Z:F1}){(moving ? " moving" : "")}");
}

return 0;
