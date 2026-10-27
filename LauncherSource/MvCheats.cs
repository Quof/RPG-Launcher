using System.Globalization;
using System.Text.Json;

namespace RpgLauncher;

internal enum MvPhase
{
    Loading,
    Title,
    InGame
}

internal sealed record MvActor(int Id, string Name, int Level, int MaxLevel, long Exp, long NextExp, long Hp, long Mp, long[] Params);

internal sealed record MvState(MvPhase Phase, long Gold, bool Noclip, IReadOnlyList<MvActor> Actors);

/// <summary>Changes to one actor; null leaves a value as it is.</summary>
internal sealed record MvActorChanges(int Id, long? Level, long? Exp, long? Hp, long? Mp, long?[] Params);

/// <summary>Reads and changes party data in a running RPG Maker MV or MZ game through its global game objects.</summary>
internal static class MvCheats
{
    /// <summary>Max HP, Max MP, Attack, Defense, M.Attack, M.Defense, Agility, Luck: RPG Maker's parameter order.</summary>
    public static readonly string[] ParamNames = { "Max HP", "Max MP", "Attack", "Defense", "M.Attack", "M.Defense", "Agility", "Luck" };

    // Scripts are ES5: MV games on NW.js 0.12 run Chromium 41, which has no arrow functions or let.
    // "loading" until the game objects exist, "title" before a game is started or loaded.
    private const string PhaseFunction = @"
function rpgLauncherPhase() {
  if (typeof $gameParty === 'undefined' || !$gameParty || typeof $gameMap === 'undefined' || !$gameMap) return 'loading';
  var scene = typeof SceneManager !== 'undefined' ? SceneManager._scene : null;
  var title = (typeof Scene_Title === 'function' && scene instanceof Scene_Title) ||
              (typeof Scene_Boot === 'function' && scene instanceof Scene_Boot);
  return $gameMap.mapId() > 0 && !title ? 'game' : 'title';
}";

    private const string ReadScript = "(function () {" + PhaseFunction + @"
  var phase = rpgLauncherPhase();
  if (phase === 'loading') return JSON.stringify({ phase: phase });
  var actors = [], members = $gameParty.allMembers();
  for (var m = 0; m < members.length; m++) {
    var a = members[m], params = [];
    for (var i = 0; i < 8; i++) params.push(a.param(i));
    actors.push({ id: a.actorId(), name: a.name(), level: a.level, maxLevel: a.maxLevel(), exp: a.currentExp(),
                  next: a.isMaxLevel() ? -1 : a.nextLevelExp(), hp: a.hp, mp: a.mp, params: params });
  }
  var noclip = Game_Player.prototype.rpgLauncherNoclip;
  return JSON.stringify({ phase: phase, gold: $gameParty.gold(), noclip: !!(noclip && noclip.on), actors: actors });
})()";

    private const string GoldScript = @"(function (target) {
  var max = $gameParty.maxGold();
  if (target === 'max') target = isFinite(max) ? max : 99999999;
  $gameParty.gainGold(target - $gameParty.gold());
  return String($gameParty.gold());
})";

    // Parameters are raised through the actor's own bonus (addParam), so equipment and traits still apply on top.
    // Targets are kept within the game's limits (MV caps most at 999). Extra passes absorb rounding and plugin
    // formulas; a pass that changes nothing is undone, so no hidden bonus builds up past a cap.
    private const string ActorScript = @"(function (c) {
  var a = $gameActors.actor(c.id);
  if (!a) return 'That party member is no longer in the game.';
  if (c.level !== null) a.changeLevel(c.level, false);
  if (c.exp !== null) a.changeExp(c.exp, false);
  for (var i = 0; i < 8; i++) {
    if (c.params[i] === null) continue;
    var min = a.paramMin ? Number(a.paramMin(i)) : 0, max = a.paramMax ? Number(a.paramMax(i)) : Infinity;
    if (isNaN(min)) min = 0;
    if (isNaN(max)) max = Infinity;
    var target = Math.min(Math.max(c.params[i], min), max);
    for (var pass = 0; pass < 4; pass++) {
      var current = a.param(i);
      var rate = a.paramRate(i) * a.paramBuffRate(i);
      if (current === target || !(rate > 0)) break;
      var plus = Math.round((target - current) / rate);
      if (plus === 0 || !isFinite(plus)) break;
      a.addParam(i, plus);
      if (a.param(i) === current) { a.addParam(i, -plus); break; }
    }
  }
  if (c.hp !== null) a.setHp(c.hp);
  if (c.mp !== null) a.setMp(c.mp);
  return '';
})";

    private const string HealScript = @"(function () {
  var members = $gameParty.allMembers();
  for (var i = 0; i < members.length; i++) members[i].recoverAll();
  return '';
})()";

    // Noclip uses the player's debug walk-through check (Ctrl in a playtest), which events and vehicles don't reset.
    private const string NoclipScript = @"(function (on) {
  var proto = Game_Player.prototype;
  if (!proto.rpgLauncherNoclip) {
    var original = proto.isDebugThrough;
    proto.rpgLauncherNoclip = { on: false };
    proto.isDebugThrough = function () {
      return proto.rpgLauncherNoclip.on || (original ? original.call(this) : false);
    };
  }
  proto.rpgLauncherNoclip.on = on;
  return String(on);
})";

    public static async Task<MvState> ReadAsync(CheatBridge bridge, CancellationToken token)
    {
        string json = await bridge.EvaluateAsync(ReadScript, token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var phase = ParsePhase(Get(root, "phase") is { ValueKind: JsonValueKind.String } p ? p.GetString() : null);
        if (phase == MvPhase.Loading) return new MvState(phase, 0, false, Array.Empty<MvActor>());

        // Plugins can leave values undefined, which JSON drops: missing numbers read as 0.
        var actors = new List<MvActor>();
        if (Get(root, "actors") is { ValueKind: JsonValueKind.Array } list)
        {
            foreach (var a in list.EnumerateArray())
            {
                var listed = Get(a, "params") is { ValueKind: JsonValueKind.Array } values
                    ? values.EnumerateArray().Select(Number).ToArray()
                    : Array.Empty<long>();
                actors.Add(new MvActor(
                    (int)Number(Get(a, "id")),
                    Get(a, "name") is { ValueKind: JsonValueKind.String } name ? name.GetString() ?? "" : "",
                    (int)Number(Get(a, "level")),
                    (int)Number(Get(a, "maxLevel")),
                    Number(Get(a, "exp")),
                    Number(Get(a, "next")),
                    Number(Get(a, "hp")),
                    Number(Get(a, "mp")),
                    Enumerable.Range(0, ParamNames.Length).Select(i => i < listed.Length ? listed[i] : 0).ToArray()));
            }
        }
        return new MvState(phase, Number(Get(root, "gold")), Get(root, "noclip").ValueKind == JsonValueKind.True, actors);
    }

    /// <summary>Sets the party's gold (the game caps it at its maximum); null sets the maximum. Returns the new amount.</summary>
    public static async Task<long> SetGoldAsync(CheatBridge bridge, long? gold, CancellationToken token)
    {
        string argument = gold is { } value ? value.ToString(CultureInfo.InvariantCulture) : "'max'";
        string result = await bridge.EvaluateAsync($"{GoldScript}({argument})", token).ConfigureAwait(false);
        return double.TryParse(result, NumberStyles.Float, CultureInfo.InvariantCulture, out double amount)
            ? (long)Math.Round(amount)
            : 0;
    }

    /// <summary>Applies the changes; returns an error message, or null.</summary>
    public static async Task<string?> ApplyAsync(CheatBridge bridge, MvActorChanges changes, CancellationToken token)
    {
        string json = JsonSerializer.Serialize(new
        {
            id = changes.Id,
            level = changes.Level,
            exp = changes.Exp,
            hp = changes.Hp,
            mp = changes.Mp,
            @params = changes.Params
        });
        string error = await bridge.EvaluateAsync($"{ActorScript}({json})", token).ConfigureAwait(false);
        return error.Length == 0 ? null : error;
    }

    public static Task HealPartyAsync(CheatBridge bridge, CancellationToken token) =>
        bridge.EvaluateAsync(HealScript, token);

    public static Task SetNoclipAsync(CheatBridge bridge, bool on, CancellationToken token) =>
        bridge.EvaluateAsync($"{NoclipScript}({(on ? "true" : "false")})", token);

    private static MvPhase ParsePhase(string? phase) => phase switch
    {
        "game" => MvPhase.InGame,
        "title" => MvPhase.Title,
        _ => MvPhase.Loading
    };

    private static JsonElement Get(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    // Plugins sometimes leave fractions; non-numbers count as 0.
    private static long Number(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double value) && double.IsFinite(value)
            ? (long)Math.Round(Math.Clamp(value, -1e15, 1e15))
            : 0;
}
