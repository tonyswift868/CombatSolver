using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CombatSolver;
using CombatSolver.HotKernel;
using CombatSolver.HotSearch;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Runs;

namespace OfflineSearchHarness;

internal static class SurvivalRescueChecks
{
    public static int Main(string[] args)
    {
        var (requestPath, settingsPath, output, expectation) = (args[0], args[1], args[2], args[3]);
        var settings = SolverSettings.DeserializeForTesting(File.ReadAllText(settingsPath));
        var loop = new MainLoopContext();
        GameBootstrap.EnsureBootstrapped(loop, new HarnessOptions
        {
            Profile = "Medium",
            ProductionBudget = true,
            BudgetMilliseconds = settings.MaxCombatSolveSeconds * 1000,
            MaxDegreeOfParallelism = settings.SearchMaxDegreeOfParallelism ?? SolverWeights.DefaultSearchMaxDegreeOfParallelism,
            OutputDirectory = output,
        });
        SolverSettings.ApplyForTesting(settings with
        {
            OnlineStatisticsEnabled = false,
            SearchCompletionNotificationsEnabled = false,
        });

        try
        {
            var setup = GeneratedScenarioSetup.Prepare(GeneratedScenarioSetup.ReadRequest(requestPath),
                Path.Combine(output, "scenario"));
            var entering = setup.EnterCombatRoomAsync();
            loop.RunUntilCompleted(entering, TimeSpan.FromSeconds(120), "Enter regression combat");
            using var choiceScope = entering.GetAwaiter().GetResult();
            var combat = OfflineCombat.WaitForPlayableCombat(loop);
            var player = LocalContext.GetMe(combat)!;
            setup.CaptureOpening(combat);
            if (!setup.Request.PreserveNativeCombatStateForTest)
                loop.RunUntilCompleted(setup.Session.InjectInitialStateAsync(combat, player),
                    TimeSpan.FromSeconds(30), "Set regression start state");

            var root = CombatRootSnapshot.Capture(combat);
            var names = SolverDisplayNames.Capture(combat);
            var damage = BattleDamageTracker.Observe(combat);
            var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat,
                includeTurnSetup: false, theftPolicy: setup.Request.TheftPolicyForTest ?? SolverController.ResolveTheftPolicy(combat));
            double? firstWinMs = null;
            List<string> rescueRounds = [];
            policy = policy with
            {
                MeasurePhasePerformance = true,
                Diagnostics = new SearchDiagnosticsSink(message =>
                {
                    if (!message.StartsWith("SURVIVAL_RESCUE ", StringComparison.Ordinal))
                        return;
                    rescueRounds.Add(message);
                    Console.WriteLine($"RESCUE_TICK {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} {message}");
                }, _ => { }),
            };
            using var process = Process.GetCurrentProcess();
            TimeSpan cpuStart = process.TotalProcessorTime;
            var timer = Stopwatch.StartNew();
            void Observe(SolverResult result)
            {
                if (IsVictory(result))
                    firstWinMs ??= timer.Elapsed.TotalMilliseconds;
            }
            var result = HotSearchCoordinator.Solve(root, names, damage, policy, CancellationToken.None,
                onPreliminaryResult: Observe);
            Observe(result);
            timer.Stop();
            Console.WriteLine($"RESCUE_END {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
            process.Refresh();
            double cpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
            long peakWorkingSet = process.PeakWorkingSet64;

            var route = result.BestNode.Actions;
            byte[] routeJson = JsonSerializer.SerializeToUtf8Bytes(route, UnattendedTestFiles.JsonOptions);
            bool won = IsVictory(result);
            string? nativeOutcome = null;
            int? nativeHp = null;
            if (expectation != "control" && (won || expectation is "rescue" or "defeat"))
            {
                Replay(combat, loop, route);
                nativeOutcome = RealCombatDriver.GetLiveOutcome(combat).ToString();
                nativeHp = player.Creature.CurrentHp;
            }

            var forced = policy.PotionStrategy.Directives
                .Where(directive => directive.Directive == SolverPotionDirective.Force)
                .Select(directive => $"{directive.Slot}:{directive.PotionId}").ToArray();
            var used = route.Where(a => a.Kind == PlanActionKind.UsePotion)
                .Select(a => $"{a.PotionSlot}:{a.PotionId}").ToArray();
            string? english = null, chinese = null;
            if (expectation is "rescue" or "defeat")
            {
                english = SurvivalText(result, "eng");
                chinese = SurvivalText(result, "zhs");
            }
            var record = new
            {
                won,
                result.OnlyDeathRoutesFound,
                result.DeathTurn,
                Boundary = result.BoundaryReason.ToString(),
                result.TotalExpandedNodes,
                result.TotalTransitionCount,
                result.Snapshot.PlayerHp,
                StartHp = root.HotRoot.RootState.Player.CurrentHp,
                FirstWinMs = firstWinMs,
                SolveMs = timer.Elapsed.TotalMilliseconds,
                CpuMs = cpuMs,
                PeakWorkingSetBytes = peakWorkingSet,
                BudgetMs = settings.MaxCombatSolveSeconds * 1000,
                ConfiguredNodeBudget = settings.SearchMaxExpandedNodes,
                Dop = policy.MaxDegreeOfParallelism,
                RescueRounds = rescueRounds,
                RouteSha = Convert.ToHexString(SHA256.HashData(routeJson)),
                Root = root.ContinuationStamp.StateText,
                forced, used, nativeOutcome, nativeHp, english, chinese,
                ServerGC = System.Runtime.GCSettings.IsServerGC,
                GcLatency = System.Runtime.GCSettings.LatencyMode.ToString(),
            };
            File.WriteAllBytes(Path.Combine(output, "route.json"), routeJson);
            File.WriteAllText(Path.Combine(output, "result.json"),
                JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{setup.Request.ScenarioId}: victory={won} onlyDeath={result.OnlyDeathRoutesFound} "
                + $"boundary={result.BoundaryReason} deathTurn={result.DeathTurn} native={nativeOutcome}:{nativeHp}");

            bool validWin = won && !result.OnlyDeathRoutesFound && result.BoundaryReason == SearchBoundaryReason.None
                && nativeOutcome == nameof(HotCombatOutcome.Victory) && nativeHp > 0 && nativeHp == result.Snapshot.PlayerHp
                && forced.All(used.Contains);
            bool hitConfiguredBudget = result.BoundaryReason == SearchBoundaryReason.TimeLimit
                && timer.Elapsed.TotalMilliseconds >= record.BudgetMs
                || result.BoundaryReason == SearchBoundaryReason.NodeLimit
                && record.ConfiguredNodeBudget is int nodes && result.TotalExpandedNodes >= nodes;
            bool valid = expectation switch
            {
                "control" => won && rescueRounds.Count == 0,
                "victory" => validWin,
                "forced-victory" => validWin && forced.ToHashSet().SetEquals(["0:POWER_POTION", "1:COLORLESS_POTION"]),
                "rescue" => validWin || (!won && hitConfiguredBudget && !result.OnlyDeathRoutesFound
                    && result.DeathTurn.HasValue && nativeOutcome == nameof(HotCombatOutcome.Defeat)
                    && chinese == "搜索未覆盖完整，暂无生还路线" && english == "Search incomplete. No surviving route found yet."),
                "defeat" => !won && result.OnlyDeathRoutesFound && result.DeathTurn.HasValue
                    && result.BoundaryReason == SearchBoundaryReason.None && nativeOutcome == nameof(HotCombatOutcome.Defeat)
                    && chinese == "未找到生还路线" && english == "No surviving route found",
                _ => throw new ArgumentException($"Unknown expectation {expectation}"),
            };
            if (!valid)
                Console.Error.WriteLine($"Expected {expectation}; got the result above.");
            return valid ? 0 : 1;
        }
        finally
        {
            RealCombatDriver.CleanupActiveRun();
            ModRuntime.Session?.Dispose();
            ModRuntime.FlushDiagnostics();
        }
    }

    private static bool IsVictory(SolverResult result)
        => result.Snapshot.AllEnemiesDead && result.Snapshot.PlayerHp > 0;

    private static string? SurvivalText(SolverResult result, string language)
    {
        OfflineLocalization.Install(language);
        var snapshot = SolverOverlaySnapshot.Capture(result, unexpectedReplan: false);
        var property = typeof(SolverOverlaySnapshot).GetProperty("SurvivalOutcomeText");
        return property != null ? (string?)property.GetValue(snapshot)
            : snapshot.OnlyDeathRoutesFound ? SolverText.Get("未找到生还路线") : null;
    }

    private static void Replay(CombatState combat, MainLoopContext loop, IReadOnlyList<PlanAction> route)
    {
        var player = LocalContext.GetMe(combat)!;
        foreach (var action in route)
        {
            if (RealCombatDriver.GetLiveOutcome(combat) != HotCombatOutcome.Ongoing
                || player.PlayerCombatState!.TurnNumber != action.Turn)
                throw new InvalidDataException($"Replay timing drift at {action.Turn}:{action.Kind}.");

            var target = combat.GetCreature(action.TargetCombatId);
            var liveAction = action.Kind switch
            {
                PlanActionKind.UsePotion => RealCombatAction.UsePotion(
                    player.GetPotionAtSlotIndex(action.PotionSlot) is { } potion && potion.Id.Entry == action.PotionId
                        ? potion : throw new InvalidDataException($"Replay potion mismatch at slot {action.PotionSlot}."),
                    target),
                PlanActionKind.PlayCard => RealCombatAction.PlayCard(
                    SolverController.FindCardForDeployment(player.PlayerCombatState.Hand.Cards.ToList(), action), target),
                PlanActionKind.EndTurn => RealCombatAction.EndTurn(),
                _ => throw new InvalidDataException($"Unexpected replay action {action.Kind}."),
            };
            List<PlanCardChoice> choices = [.. action.GetActionChoicesInExecutionOrder()];
            if (action.TurnStartChoices is { } turnChoices)
                choices.AddRange(turnChoices);
            var selector = new PlanDrivenCardSelector(choices);
            using var scope = RunManager.Instance.CardSelectorStack.PushSelector(player, selector);
            var outcome = RealCombatDriver.RunRealCombatAction(loop, combat, liveAction,
                externalErrorProbe: () => selector.LastError);
            if (action.Kind == PlanActionKind.UsePotion && player.GetPotionAtSlotIndex(action.PotionSlot) != null)
                throw new InvalidDataException($"Replay did not consume potion in slot {action.PotionSlot}.");
            Console.WriteLine($"{action.Turn}:{action.Kind}:{action.CardId}{action.PotionId}:"
                + $"slot={action.PotionSlot} hp={player.Creature.CurrentHp} outcome={outcome}");
        }
    }
}
