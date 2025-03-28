using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using RetakesPlugin.Modules;
using RetakesPluginShared.Enums;
using RetakesPlugin.Modules.Configs;
using RetakesPlugin.Modules.Managers;
using RetakesPluginShared;
using RetakesPluginShared.Events;
using Helpers = RetakesPlugin.Modules.Helpers;

namespace RetakesPlugin;

[MinimumApiVersion(220)]
public class RetakesPlugin : BasePlugin
{
    private const string Version = "2.0.14";

    #region Plugin info
    public override string ModuleName => "Retakes Plugin";
    public override string ModuleVersion => Version;
    public override string ModuleAuthor => "B3none";
    public override string ModuleDescription => "https://github.com/b3none/cs2-retakes";
    #endregion

    #region Constants
    public static readonly string LogPrefix = $"[Retakes {Version}] ";

    // These two static variables are overwritten in the Load / OnMapStart with config values.
    public static string MessagePrefix = $"[{ChatColors.Green}Retakes{ChatColors.White}] ";
    public static bool IsDebugMode;
    #endregion

    #region Helpers
    private Translator _translator;
    private GameManager? _gameManager;
    private SpawnManager? _spawnManager;
    private BreakerManager? _breakerManager;

    public static PluginCapability<IRetakesPluginEventSender> RetakesPluginEventSenderCapability { get; } = new("retakes_plugin:event_sender");
    #endregion

    #region Configs
    private MapConfig? _mapConfig;
    private RetakesConfig? _retakesConfig;
    #endregion

    #region State
    private Bombsite _currentBombsite = Bombsite.A;
    private CCSPlayerController? _planter;
    private CsTeam _lastRoundWinner = CsTeam.None;
    private Bombsite? _showingSpawnsForBombsite;
    private Bombsite? _forcedBombsite;

    // TODO: We should really store this in SQLite, but for now we'll just store it in memory.
    private readonly HashSet<CCSPlayerController> _hasMutedVoices = [];

    private void ResetState()
    {
        _currentBombsite = Bombsite.A;
        _planter = null;
        _lastRoundWinner = CsTeam.None;
        _showingSpawnsForBombsite = null;
    }
    #endregion

    public RetakesPlugin()
    {
        _translator = new Translator(Localizer);
    }

    public override void Load(bool hotReload)
    {
        _translator = new Translator(Localizer);

        MessagePrefix = _translator["retakes.prefix"];

        Helpers.Debug($"Plugin loaded!");

        RegisterListener<Listeners.OnMapStart>(mapName =>
        {
            OnMapStart(mapName);
        });

        var retakesPluginEventSender = new RetakesPluginEventSender();
        Capabilities.RegisterPluginCapability(RetakesPluginEventSenderCapability, () => retakesPluginEventSender);

        if (hotReload)
        {
            Server.PrintToChatAll($"{LogPrefix}Update detected, restarting map...");
            Server.ExecuteCommand($"map {Server.MapName}");
        }
    }

    [ConsoleCommand("jointeam", "Join team override for retakes")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnCommandJoinTeam(CCSPlayerController player, CommandInfo command)
    {
        Console.WriteLine($"[Retakes] Player {player.PlayerName} tried to join a team.");
        // 可加上 _queueManager.PlayerJoinedTeam(player, fromTeam, toTeam);
    }

    [ConsoleCommand("css_mapconfigs", "Displays a list of available map configs.")]
    [ConsoleCommand("css_viewmapconfigs", "Displays a list of available map configs.")]
    [ConsoleCommand("css_listmapconfigs", "Displays a list of available map configs.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnCommandMapConfigs(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player != null && !Helpers.IsValidPlayer(player))
        {
            return;
        }

        var mapConfigDirectory = Path.Combine(ModuleDirectory, "map_config");

        var files = Directory.GetFiles(mapConfigDirectory);

        // organise files alphabetically
        Array.Sort(files);

        if (!Directory.Exists(mapConfigDirectory) || files.Length == 0)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}No map configs found.");
            return;
        }

        foreach (var file in files)
        {
            var transformedFile = file
                .Replace($"{mapConfigDirectory}/", "")
                .Replace(".json", "");

            commandInfo.ReplyToCommand($"{MessagePrefix}!mapconfig {transformedFile}");

            // 防止 player 為 null
            player?.PrintToConsole($"{MessagePrefix}!mapconfig {transformedFile}");
        }

        commandInfo.ReplyToCommand($"{MessagePrefix}A list of available map configs has been outputted above.");
    }

    [ConsoleCommand("css_forcebombsite", "Force the retakes to occur from a single bombsite.")]
    [CommandHelper(minArgs: 1, usage: "[A/B]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnCommandForceBombsite(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player != null && !Helpers.IsValidPlayer(player))
        {
            return;
        }

        var bombsite = commandInfo.GetArg(1).ToUpper();
        if (bombsite != "A" && bombsite != "B")
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}You must specify a bombsite [A / B].");
            return;
        }

        _forcedBombsite = bombsite == "A" ? Bombsite.A : Bombsite.B;

        commandInfo.ReplyToCommand($"{MessagePrefix}The bombsite will now be forced to {_forcedBombsite}.");
    }

    [ConsoleCommand("css_forcebombsitestop", "Clear the forced bombsite and return back to normal.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnCommandForceBombsiteStop(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player != null && !Helpers.IsValidPlayer(player))
        {
            return;
        }

        _forcedBombsite = null;

        commandInfo.ReplyToCommand($"{MessagePrefix}The bombsite will no longer be forced.");
    }

    [ConsoleCommand("css_showspawns", "Show the spawns for the specified bombsite.")]
    [ConsoleCommand("css_spawns", "Show the spawns for the specified bombsite.")]
    [ConsoleCommand("css_edit", "Show the spawns for the specified bombsite.")]
    [CommandHelper(minArgs: 1, usage: "[A/B]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [RequiresPermissions("@css/root")]
    public void OnCommandShowSpawns(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player != null && !Helpers.IsValidPlayer(player))
        {
            return;
        }

        var bombsite = commandInfo.GetArg(1).ToUpper();
        if (bombsite != "A" && bombsite != "B")
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}You must specify a bombsite [A / B].");
            return;
        }

        if (_mapConfig == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Map config not loaded for some reason...");
            return;
        }

        _showingSpawnsForBombsite = bombsite == "A" ? Bombsite.A : Bombsite.B;

        // This will fire the OnRoundStart event listener
        Server.ExecuteCommand("mp_warmup_start");
        Server.ExecuteCommand("mp_warmuptime 120");
        Server.ExecuteCommand("mp_warmup_pausetimer 1");
    }

    [ConsoleCommand("css_add", "Creates a new retakes spawn for the bombsite currently shown.")]
    [ConsoleCommand("css_addspawn", "Creates a new retakes spawn for the bombsite currently shown.")]
    [ConsoleCommand("css_new", "Creates a new retakes spawn for the bombsite currently shown.")]
    [ConsoleCommand("css_newspawn", "Creates a new retakes spawn for the bombsite currently shown.")]
    [CommandHelper(minArgs: 1, usage: "[T/CT] [Y/N can be planter]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [RequiresPermissions("@css/root")]
    public void OnCommandAddSpawn(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (_showingSpawnsForBombsite == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}You can't add a spawn if you're not showing the spawns.");
            return;
        }

        if (_spawnManager == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Spawn manager not loaded for some reason...");
            return;
        }

        if (!Helpers.DoesPlayerHaveAlivePawn(player))
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}You must have an alive player pawn.");
            return;
        }

        var team = commandInfo.GetArg(1).ToUpper();
        if (team != "T" && team != "CT")
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}You must specify a team [T / CT] - [Value: {team}].");
            return;
        }

        var canBePlanterInput = commandInfo.GetArg(2).ToUpper();
        if (!string.IsNullOrWhiteSpace(canBePlanterInput) && canBePlanterInput != "Y" && canBePlanterInput != "N")
        {
            commandInfo.ReplyToCommand(
                $"{MessagePrefix}Incorrect value passed for can be a planter [Y / N] - [Value: {canBePlanterInput}].");
            return;
        }

        var spawns = _spawnManager.GetSpawns((Bombsite)_showingSpawnsForBombsite);

        var closestDistance = 9999.9;

        foreach (var spawn in spawns)
        {
            var distance = Helpers.GetDistanceBetweenVectors(spawn.Vector, player!.PlayerPawn.Value!.AbsOrigin!);

            if (distance > 128.0 || distance > closestDistance)
            {
                continue;
            }

            closestDistance = distance;
        }

        if (closestDistance <= 72)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}You are too close to another spawn, move away and try again.");
            return;
        }

        var newSpawn = new Spawn(
            vector: player!.PlayerPawn.Value!.AbsOrigin!,
            qAngle: player!.PlayerPawn.Value!.AbsRotation!
        )
        {
            Team = team == "T" ? CsTeam.Terrorist : CsTeam.CounterTerrorist,
            CanBePlanter = team == "T" && !string.IsNullOrWhiteSpace(canBePlanterInput)
                ? canBePlanterInput == "Y"
                : player.PlayerPawn.Value.InBombZoneTrigger,
            Bombsite = (Bombsite)_showingSpawnsForBombsite
        };
        Helpers.ShowSpawn(newSpawn);

        if (_mapConfig == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Map config not loaded for some reason...");
            return;
        }

        var didAddSpawn = _mapConfig.AddSpawn(newSpawn);
        if (didAddSpawn)
        {
            _spawnManager.CalculateMapSpawns();
        }

        commandInfo.ReplyToCommand($"{MessagePrefix}{(didAddSpawn ? "Spawn added" : "Error adding spawn")}");
    }

    [ConsoleCommand("css_remove", "Deletes the nearest retakes spawn.")]
    [ConsoleCommand("css_removespawn", "Deletes the nearest retakes spawn.")]
    [ConsoleCommand("css_delete", "Deletes the nearest retakes spawn.")]
    [ConsoleCommand("css_deletespawn", "Deletes the nearest retakes spawn.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [RequiresPermissions("@css/root")]
    public void OnCommandRemoveSpawn(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (_showingSpawnsForBombsite == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}You can't remove a spawn if you're not showing the spawns.");
            return;
        }

        if (_spawnManager == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Spawn manager not loaded for some reason...");
            return;
        }

        if (_mapConfig == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Map config not loaded for some reason...");
            return;
        }

        if (!Helpers.DoesPlayerHaveAlivePawn(player))
        {
            return;
        }

        var spawns = _spawnManager.GetSpawns((Bombsite)_showingSpawnsForBombsite);

        if (spawns.Count == 0)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}No spawns found.");
            return;
        }

        var closestDistance = 9999.9;
        Spawn? closestSpawn = null;

        foreach (var spawn in spawns)
        {
            var distance = Helpers.GetDistanceBetweenVectors(spawn.Vector, player!.PlayerPawn.Value!.AbsOrigin!);

            if (distance > 128.0 || distance > closestDistance)
            {
                continue;
            }

            closestDistance = distance;
            closestSpawn = spawn;
        }

        if (closestSpawn == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}No spawns found within 128 units.");
            return;
        }

        // Remove the beam entity that is showing for the closest spawn.
        var beamEntities = Utilities.FindAllEntitiesByDesignerName<CBeam>("beam");
        foreach (var beamEntity in beamEntities)
        {
            if (beamEntity.AbsOrigin == null)
            {
                continue;
            }

            if (
                beamEntity.AbsOrigin.Z - closestSpawn.Vector.Z == 0 &&
                beamEntity.AbsOrigin.X - closestSpawn.Vector.X == 0 &&
                beamEntity.AbsOrigin.Y - closestSpawn.Vector.Y == 0
            )
            {
                beamEntity.Remove();
            }
        }

        var didRemoveSpawn = _mapConfig.RemoveSpawn(closestSpawn);
        if (didRemoveSpawn)
        {
            _spawnManager.CalculateMapSpawns();
        }

        commandInfo.ReplyToCommand($"{MessagePrefix}{(didRemoveSpawn ? "Spawn removed" : "Error removing spawn")}");
    }

    [ConsoleCommand("css_nearestspawn", "Goes to nearest retakes spawn.")]
    [ConsoleCommand("css_nearest", "Goes to nearest retakes spawn.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [RequiresPermissions("@css/root")]
    public void OnCommandNearestSpawn(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (_showingSpawnsForBombsite == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}You can't remove a spawn if you're not showing the spawns.");
            return;
        }

        if (_spawnManager == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Spawn manager not loaded for some reason...");
            return;
        }

        if (_mapConfig == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Map config not loaded for some reason...");
            return;
        }

        if (!Helpers.DoesPlayerHaveAlivePawn(player))
        {
            return;
        }

        var spawns = _spawnManager.GetSpawns((Bombsite)_showingSpawnsForBombsite);

        if (spawns.Count == 0)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}No spawns found.");
            return;
        }

        var closestDistance = 9999.9;
        Spawn? closestSpawn = null;

        foreach (var spawn in spawns)
        {
            var distance = Helpers.GetDistanceBetweenVectors(spawn.Vector, player!.PlayerPawn.Value!.AbsOrigin!);

            if (distance > 128.0 || distance > closestDistance)
            {
                continue;
            }

            closestDistance = distance;
            closestSpawn = spawn;
        }

        if (closestSpawn == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}No spawns found within 128 units.");
            return;
        }

        player!.PlayerPawn.Value!.Teleport(closestSpawn.Vector, closestSpawn.QAngle, new Vector());
        commandInfo.ReplyToCommand($"{MessagePrefix}Teleported to nearest spawn");
    }

    [ConsoleCommand("css_hidespawns", "Exits the spawn editing mode.")]
    [ConsoleCommand("css_done", "Exits the spawn editing mode.")]
    [ConsoleCommand("css_exitedit", "Exits the spawn editing mode.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [RequiresPermissions("@css/root")]
    public void OnCommandHideSpawns(CCSPlayerController? player, CommandInfo commandInfo)
    {
        _showingSpawnsForBombsite = null;
        Server.ExecuteCommand("mp_warmup_end");
    }

    private void OnMapStart(string mapName)
    {
        Console.WriteLine($"[Retakes] OnMapStart called for map: {mapName}");
        // 你可以在這裡初始化 spawnManager、mapConfig 等
    }
}
