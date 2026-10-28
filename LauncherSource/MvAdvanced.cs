using System.Globalization;
using System.Text.Json;

namespace RpgLauncher;

internal sealed record MvVariable(int Id, string Name, string Value);

internal sealed record MvSwitch(int Id, string Name, bool On);

internal enum MvItemKind
{
    Item,
    Weapon,
    Armor
}

internal sealed record MvItem(int Id, string Name, int Count, int Max, bool Key);

internal sealed record MvCommonEvent(int Id, string Name, int Trigger, int Commands);

/// <summary>An event on the current map; <see cref="Page"/> is the active page (1-based), 0 when none is.</summary>
internal sealed record MvMapEvent(int Id, string Name, int X, int Y, int Page, int Pages, string SelfSwitches);

internal sealed record MvMap(int Id, string Name, IReadOnlyList<MvMapEvent> Events);

/// <summary>
/// The cheat window's Advanced tools for MV and MZ: variables, switches, items, common and map events, the game's own
/// save and load screens, and a script console. Actions return an error message for the user, or null when done.
/// </summary>
internal static class MvAdvanced
{
    // Scripts are ES5 for NW.js 0.12 (Chromium 41). Events and save screens only work while the map is showing.
    private const string OnMap =
        "if (!(SceneManager._scene instanceof Scene_Map)) return 'Close menus and battles first: this works on the map.';";

    private const string NotChanging =
        "if (SceneManager.isSceneChanging()) return 'Wait a moment: the game is changing screens.';";

    // The lists need the database and the game objects; at the title those hold a new game's values.
    private const string Loaded = "if (!$dataSystem || !$gameVariables) throw new Error('The game is still loading.');";

    private const string VariablesScript = @"(function () {
  " + Loaded + @"
  var names = $dataSystem.variables, out = [];
  for (var i = 1; i < names.length; i++) {
    var v = $gameVariables._data[i], text;
    if (v === undefined || v === null) text = '0';
    else if (typeof v === 'number') text = String(v);
    else { try { text = JSON.stringify(v); } catch (e) { text = String(v); } }
    out.push([i, names[i] || '', text === undefined ? String(v) : text]);
  }
  return JSON.stringify(out);
})()";

    // Numbers stay numbers; JSON (a quoted text, list, object, true, false or null) is read as JSON; anything else is text.
    private const string SetVariableScript = @"(function (id, text) {
  if (id < 1 || id >= $dataSystem.variables.length) return 'That variable is not in this game.';
  var t = String(text).trim(), v;
  if (/^[-+]?\d+(\.\d+)?$/.test(t)) v = Number(t);
  else if (/^(true|false|null)$/.test(t) || /^[\[{""]/.test(t)) {
    try { v = JSON.parse(t); } catch (e) { return 'That isn\'t valid JSON: ' + e.message; }
  }
  else v = t;
  $gameVariables.setValue(id, v);
  return '';
})";

    private const string SwitchesScript = @"(function () {
  " + Loaded + @"
  var names = $dataSystem.switches, out = [];
  for (var i = 1; i < names.length; i++) out.push([i, names[i] || '', $gameSwitches.value(i) ? 1 : 0]);
  return JSON.stringify(out);
})()";

    private const string SetSwitchScript = @"(function (id, on) {
  if (id < 1 || id >= $dataSystem.switches.length) return 'That switch is not in this game.';
  $gameSwitches.setValue(id, on);
  return '';
})";

    private const string ItemList = "var list = kind === 'weapon' ? $dataWeapons : kind === 'armor' ? $dataArmors : $dataItems;";

    private const string ItemsScript = @"(function (kind) {
  " + Loaded + @"
  " + ItemList + @"
  var out = [];
  for (var i = 1; i < list.length; i++) {
    var item = list[i];
    if (!item || !item.name) continue;
    out.push([i, item.name, $gameParty.numItems(item), $gameParty.maxItems(item), kind === 'item' && item.itypeId === 2 ? 1 : 0]);
  }
  return JSON.stringify(out);
})";

    // Counts change through gainItem, so the game's own limits apply; equipped weapons and armor stay equipped.
    private const string SetItemScript = @"(function (kind, id, count) {
  " + ItemList + @"
  var item = list[id];
  if (!item) return 'That item is not in this game.';
  var target = Math.max(0, Math.min(count, $gameParty.maxItems(item)));
  $gameParty.gainItem(item, target - $gameParty.numItems(item));
  return '';
})";

    private const string CommonEventsScript = @"(function () {
  " + Loaded + @"
  var out = [];
  for (var i = 1; i < $dataCommonEvents.length; i++) {
    var e = $dataCommonEvents[i];
    if (e) out.push([i, e.name || '', e.trigger || 0, e.list ? Math.max(0, e.list.length - 1) : 0]);
  }
  return JSON.stringify(out);
})()";

    // Reserved common events run on the map as soon as no other event is running.
    private const string RunCommonEventScript = @"(function (id) {
  " + OnMap + @"
  if (!$dataCommonEvents[id]) return 'That common event is not in this game.';
  $gameTemp.reserveCommonEvent(id);
  return '';
})";

    private const string MapEventsScript = @"(function () {
  " + Loaded + @"
  if (!$dataMap || !($gameMap.mapId() > 0)) throw new Error('No map yet: start or load a game first.');
  var mapId = $gameMap.mapId(), info = $dataMapInfos[mapId], out = [], events = $gameMap.events();
  for (var i = 0; i < events.length; i++) {
    var e = events[i], data = e.event();
    if (!data || !data.pages) continue;
    var on = '';
    for (var k = 0; k < 4; k++) if ($gameSelfSwitches.value([mapId, e.eventId(), 'ABCD'.charAt(k)])) on += 'ABCD'.charAt(k);
    out.push([e.eventId(), data.name || '', e.x, e.y, e._pageIndex + 1, data.pages.length, on]);
  }
  return JSON.stringify({ id: mapId, name: $gameMap.displayName() || (info ? info.name : ''), events: out });
})()";

    // The page whose conditions hold now, started the way the player would start it.
    private const string RunEventScript = @"(function (id) {
  " + OnMap + @"
  var e = $gameMap.event(id);
  if (!e || !e.event()) return 'That event is not on this map any more.';
  if (e._pageIndex < 0) return 'None of this event\'s pages is active now; replay one of them instead.';
  if ($gameMap.isEventRunning()) return 'Another event is running; try again when it ends.';
  if (e.list().length <= 1) return 'This event\'s active page has no commands.';
  e.start();
  return '';
})";

    // Any page, whatever its conditions: runs its commands as the event, as if that page were active.
    private const string ReplayPageScript = @"(function (id, page) {
  " + OnMap + @"
  var e = $gameMap.event(id);
  if (!e || !e.event()) return 'That event is not on this map any more.';
  var p = e.event().pages[page];
  if (!p || !p.list || p.list.length <= 1) return 'That page has no commands.';
  if ($gameMap.isEventRunning()) return 'Another event is running; try again when it ends.';
  $gameMap._interpreter.setup(p.list, id);
  return '';
})";

    // Turning the self switches off lets events that run once (most cutscenes) run again.
    private const string ResetSelfSwitchesScript = @"(function (id) {
  var mapId = $gameMap.mapId();
  for (var k = 0; k < 4; k++) $gameSelfSwitches.setValue([mapId, id, 'ABCD'.charAt(k)], false);
  return '';
})";

    // The game's own screens. Scene_Save itself never checks whether the game has turned saving off.
    private const string SaveScreenScript = @"(function () {
  " + OnMap + @"
  " + NotChanging + @"
  SceneManager.push(Scene_Save);
  return '';
})()";

    private const string LoadScreenScript = @"(function () {
  var s = SceneManager._scene;
  if (!(s instanceof Scene_Map) && !(s instanceof Scene_Title)) return 'Close menus and battles first: this works on the map or the title screen.';
  " + NotChanging + @"
  SceneManager.push(Scene_Load);
  return '';
})()";

    private const string AtTitleScript = "(function () {" + MvCheats.PhaseFunction + " return String(rpgLauncherPhase() === 'title'); })()";

    // MZ stops updating while its window isn't active (MV keeps going).
    private const string PausedScript =
        "String(typeof SceneManager.isGameActive === 'function' && !SceneManager.isGameActive())";

    private const string SaveEnabledScript = @"(function () {
  " + Loaded + @"
  return $gameSystem.isSaveEnabled() ? '1' : '0';
})()";

    // What the game's menu offers; events that turn saving off or on later still do.
    private const string SetSaveEnabledScript = @"(function (on) {
  " + Loaded + @"
  if (on) $gameSystem.enableSave(); else $gameSystem.disableSave();
  return '';
})";

    // Runs code in the game's global scope, as its console would, and shows the result: text quoted, objects as JSON
    // (repeated or circular parts marked, functions and typed arrays named, other kinds of object by kind), long
    // results cut.
    private const string ConsoleScript = @"(function (code) {
  var seen = [], budget = 100000;
  function kind(v) { return Object.prototype.toString.call(v); }
  function view(v) {
    return ArrayBuffer.isView && ArrayBuffer.isView(v) ? '[' + kind(v).slice(8, -1) + (v.length >= 0 ? ' of ' + v.length : '') + ']' : null;
  }
  function show(v) {
    if (v === undefined) return 'undefined';
    if (typeof v === 'function') return String(v);
    if (typeof v !== 'object' || v === null) return typeof v === 'string' ? JSON.stringify(v) : String(v);
    if (view(v)) return view(v);
    try {
      var text = JSON.stringify(v, function (key, x) {
        if (--budget < 0) return '…';
        if (typeof x === 'function') return '[function]';
        if (typeof x === 'object' && x !== null) {
          if (view(x)) return view(x);
          if (seen.indexOf(x) >= 0) return '[seen above]';
          if (seen.length > 4000) return '[…]';
          seen.push(x);
        }
        return x;
      }, 2);
      return text === undefined || text === '{}' && kind(v) !== '[object Object]' ? kind(v) : text;
    } catch (e) {
      return kind(v);
    }
  }
  var out = show((0, eval)(code));
  return out.length > 20000 ? out.slice(0, 20000) + '\n… (' + out.length + ' characters in all)' : out;
})";

    // ---- reading ----

    public static async Task<IReadOnlyList<MvVariable>> VariablesAsync(CheatBridge bridge, CancellationToken token) =>
        Read(await bridge.EvaluateAsync(VariablesScript, token).ConfigureAwait(false), root => root.EnumerateArray()
            .Select(row => new MvVariable(Int(row[0]), Text(row[1]), Text(row[2])))
            .ToList());

    public static async Task<IReadOnlyList<MvSwitch>> SwitchesAsync(CheatBridge bridge, CancellationToken token) =>
        Read(await bridge.EvaluateAsync(SwitchesScript, token).ConfigureAwait(false), root => root.EnumerateArray()
            .Select(row => new MvSwitch(Int(row[0]), Text(row[1]), Int(row[2]) != 0))
            .ToList());

    public static async Task<IReadOnlyList<MvItem>> ItemsAsync(CheatBridge bridge, MvItemKind kind, CancellationToken token) =>
        Read(await bridge.EvaluateAsync($"{ItemsScript}('{KindName(kind)}')", token).ConfigureAwait(false), root => root.EnumerateArray()
            .Select(row => new MvItem(Int(row[0]), Text(row[1]), Int(row[2]), Int(row[3]), Int(row[4]) != 0))
            .ToList());

    public static async Task<IReadOnlyList<MvCommonEvent>> CommonEventsAsync(CheatBridge bridge, CancellationToken token) =>
        Read(await bridge.EvaluateAsync(CommonEventsScript, token).ConfigureAwait(false), root => root.EnumerateArray()
            .Select(row => new MvCommonEvent(Int(row[0]), Text(row[1]), Int(row[2]), Int(row[3])))
            .ToList());

    public static async Task<MvMap> MapEventsAsync(CheatBridge bridge, CancellationToken token) =>
        Read(await bridge.EvaluateAsync(MapEventsScript, token).ConfigureAwait(false), root =>
        {
            var events = root.GetProperty("events").EnumerateArray()
                .Select(row => new MvMapEvent(Int(row[0]), Text(row[1]), Int(row[2]), Int(row[3]), Int(row[4]), Int(row[5]), Text(row[6])))
                .ToList();
            return new MvMap(Int(root.GetProperty("id")), Text(root.GetProperty("name")), events);
        });

    /// <summary>
    /// Whether no game is under way (the title screen, or a splash screen before it): the game then holds a new game's
    /// values, replaced when one starts or loads.
    /// </summary>
    public static async Task<bool> AtTitleAsync(CheatBridge bridge, CancellationToken token) =>
        await bridge.EvaluateAsync(AtTitleScript, token).ConfigureAwait(false) == "true";

    /// <summary>Whether the game is paused because its window isn't active (MZ), so an event started now waits for a click on it.</summary>
    public static async Task<bool> PausedAsync(CheatBridge bridge, CancellationToken token) =>
        await bridge.EvaluateAsync(PausedScript, token).ConfigureAwait(false) == "true";

    /// <summary>Whether the game's menu offers saving now.</summary>
    public static async Task<bool> SaveEnabledAsync(CheatBridge bridge, CancellationToken token) =>
        await bridge.EvaluateAsync(SaveEnabledScript, token).ConfigureAwait(false) == "1";

    // ---- changing ----

    public static Task<string?> SetVariableAsync(CheatBridge bridge, int id, string text, CancellationToken token) =>
        ActAsync(bridge, $"{SetVariableScript}({id}, {JsonSerializer.Serialize(text)})", token);

    public static Task<string?> SetSwitchAsync(CheatBridge bridge, int id, bool on, CancellationToken token) =>
        ActAsync(bridge, $"{SetSwitchScript}({id}, {(on ? "true" : "false")})", token);

    public static Task<string?> SetItemCountAsync(CheatBridge bridge, MvItemKind kind, int id, long count, CancellationToken token) =>
        ActAsync(bridge, $"{SetItemScript}('{KindName(kind)}', {id}, {count.ToString(CultureInfo.InvariantCulture)})", token);

    public static Task<string?> RunCommonEventAsync(CheatBridge bridge, int id, CancellationToken token) =>
        ActAsync(bridge, $"{RunCommonEventScript}({id})", token);

    public static Task<string?> RunEventAsync(CheatBridge bridge, int id, CancellationToken token) =>
        ActAsync(bridge, $"{RunEventScript}({id})", token);

    /// <param name="page">Zero-based page index.</param>
    public static Task<string?> ReplayPageAsync(CheatBridge bridge, int id, int page, CancellationToken token) =>
        ActAsync(bridge, $"{ReplayPageScript}({id}, {page})", token);

    public static Task<string?> ResetSelfSwitchesAsync(CheatBridge bridge, int id, CancellationToken token) =>
        ActAsync(bridge, $"{ResetSelfSwitchesScript}({id})", token);

    public static Task<string?> SetSaveEnabledAsync(CheatBridge bridge, bool on, CancellationToken token) =>
        ActAsync(bridge, $"{SetSaveEnabledScript}({(on ? "true" : "false")})", token);

    public static Task<string?> OpenSaveScreenAsync(CheatBridge bridge, CancellationToken token) =>
        ActAsync(bridge, SaveScreenScript, token);

    public static Task<string?> OpenLoadScreenAsync(CheatBridge bridge, CancellationToken token) =>
        ActAsync(bridge, LoadScreenScript, token);

    /// <summary>Runs script in the game and returns its result as text; a script error throws <see cref="CheatBridgeException"/>.</summary>
    public static Task<string> RunScriptAsync(CheatBridge bridge, string code, CancellationToken token) =>
        bridge.EvaluateAsync($"{ConsoleScript}({JsonSerializer.Serialize(code)})", token);

    private static async Task<string?> ActAsync(CheatBridge bridge, string script, CancellationToken token)
    {
        string error = await bridge.EvaluateAsync(script, token).ConfigureAwait(false);
        return error.Length == 0 ? null : error;
    }

    /// <summary>Reads the game's JSON answer; an answer of another shape is reported like a failed script.</summary>
    private static T Read<T>(string json, Func<JsonElement, T> read)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return read(doc.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException)
        {
            throw new CheatBridgeException("The game sent back something unexpected.", fromGame: true);
        }
    }

    private static string KindName(MvItemKind kind) => kind switch
    {
        MvItemKind.Weapon => "weapon",
        MvItemKind.Armor => "armor",
        _ => "item"
    };

    private static int Int(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double value) && double.IsFinite(value)
            ? (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue)
            : 0;

    private static string Text(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.ToString();
}
