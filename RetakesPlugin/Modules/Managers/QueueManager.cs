using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace RetakesPlugin.Modules.Managers;

public class QueueManager
{
    private readonly Translator _translator;
    private readonly int _maxRetakesPlayers;
    private readonly float _terroristRatio;
    private readonly string[] _queuePriorityFlags;
    private readonly bool _shouldForceEvenTeamsWhenPlayerCountIsMultipleOf10;
    private readonly bool _shouldPreventTeamChangesMidRound;

    public HashSet<CCSPlayerController> QueuePlayers = [];
    public HashSet<CCSPlayerController> ActivePlayers = [];

    private List<CCSPlayerController> _roundTerrorists = [];
    private List<CCSPlayerController> _roundCounterTerrorists = [];

    public QueueManager(
        Translator translator,
        int? retakesMaxPlayers,
        float? retakesTerroristRatio,
        string? queuePriorityFlags,
        bool? shouldForceEvenTeamsWhenPlayerCountIsMultipleOf10,
        bool? shouldPreventTeamChangesMidRound
    )
    {
        _translator = translator;
        _maxRetakesPlayers = retakesMaxPlayers ?? 9;
        _terroristRatio = retakesTerroristRatio ?? 0.45f;
        _queuePriorityFlags = queuePriorityFlags?.Split(",").Select(flag => flag.Trim()).ToArray() ?? ["@css/vip"];
        _shouldForceEvenTeamsWhenPlayerCountIsMultipleOf10 = shouldForceEvenTeamsWhenPlayerCountIsMultipleOf10 ?? true;
        _shouldPreventTeamChangesMidRound = shouldPreventTeamChangesMidRound ?? false;

        Console.WriteLine($"[Retakes] shouldPreventTeamChangesMidRound = {_shouldPreventTeamChangesMidRound}");
    }

    public void Update() { } // 👈 新增空方法避免錯誤

    public int GetTargetNumTerrorists()
    {
        var forceEven = _shouldForceEvenTeamsWhenPlayerCountIsMultipleOf10 && ActivePlayers.Count % 10 == 0;
        var ratio = (forceEven ? 0.5 : _terroristRatio) * ActivePlayers.Count;
        var numTerrorists = (int)Math.Round(ratio);
        return numTerrorists > 0 ? numTerrorists : 1;
    }

    public int GetTargetNumCounterTerrorists() => ActivePlayers.Count - GetTargetNumTerrorists();

    public void SetRoundTeams()
    {
        _roundTerrorists = ActivePlayers
            .Where(p => Helpers.IsValidPlayer(p) && p.Team == CsTeam.Terrorist).ToList();

        _roundCounterTerrorists = ActivePlayers
            .Where(p => Helpers.IsValidPlayer(p) && p.Team == CsTeam.CounterTerrorist).ToList();

        foreach (var player in ActivePlayers)
        {
            if (!Helpers.IsValidPlayer(player))
                continue;

            if (player.Team == CsTeam.Spectator || player.Team == CsTeam.None) // 👈 修改這裡
            {
                if (_roundTerrorists.Count <= _roundCounterTerrorists.Count)
                {
                    player.ChangeTeam(CsTeam.Terrorist);
                    _roundTerrorists.Add(player);
                }
                else
                {
                    player.ChangeTeam(CsTeam.CounterTerrorist);
                    _roundCounterTerrorists.Add(player);
                }

                Console.WriteLine($"[Retakes] Auto-assigned {player.PlayerName} to {(player.Team == CsTeam.Terrorist ? "T" : "CT")}");
            }
        }
    }

    public HookResult PlayerJoinedTeam(CCSPlayerController player, CsTeam fromTeam, CsTeam toTeam)
    {
        Helpers.Debug($"[Retakes] {player.PlayerName} tried to switch: {fromTeam} -> {toTeam}");

        if (ActivePlayers.Contains(player))
        {
            if (toTeam == CsTeam.Spectator)
            {
                RemovePlayerFromQueues(player);
                Helpers.CheckRoundDone();
                return HookResult.Continue;
            }

            if (!_shouldPreventTeamChangesMidRound)
                return HookResult.Continue;

            return HookResult.Stop;
        }

        return HookResult.Continue;
    }

    public void RemovePlayerFromQueues(CCSPlayerController player)
    {
        QueuePlayers.Remove(player);
        ActivePlayers.Remove(player);
    }

    public void ClearRoundTeams()
    {
        _roundTerrorists.Clear();
        _roundCounterTerrorists.Clear();
    }

    public void DebugQueues(string tag)
    {
        Console.WriteLine($"[Retakes][{tag}] QueuePlayers: {QueuePlayers.Count}, ActivePlayers: {ActivePlayers.Count}");
    }
}
