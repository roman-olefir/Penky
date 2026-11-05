using DurakBackend.DTOs;
using DurakBackend.Models;
using DurakBackend.Services;
using Xunit;

namespace CardGame.Tests
{
    // Боти грають тисячі партій тим самим шляхом, що й клієнт: дивляться лише у свій GameView.
    // Перевіряємо, що гра завжди доходить до кінця, не зависає і не губить карти.
    public class SimulationTests
    {
        // З надійним правилом партія не довша за кілька сотень ходів, тож стеля ловить реальні зациклення
        private const int MaxMoves = 3000;
        private static readonly RoomInfo Info = new("0000", "Симуляція", false);

        private static void AssertInvariants(GameService game)
        {
            var state = game.State;
            var total = state.Deck.Count
                + state.Table.Cards.Count
                + state.DiscardCount
                + (state.DrawnCard == null ? 0 : 1)
                + state.Players.Sum(p => p.Hand.Count + p.Penky.Count);
            Assert.Equal(36, total);

            if (state.Phase != GamePhase.InProgress) return;

            Assert.True(state.Players[state.CurrentPlayerIndex].IsActive, "Хід у гравця, який уже вибув");
            Assert.All(state.Players.Where(p => p.IsActive), p => Assert.True(p.Hand.Count + p.Penky.Count > 0));
            if (state.SubPhase == InProgressPhase.Playing)
            {
                Assert.NotEmpty(state.Players[state.CurrentPlayerIndex].Hand);
                if (state.Table.Cards.Count == 0) Assert.All(state.Players.Where(p => p.IsActive), p => Assert.NotEmpty(p.Hand));
            }
            Assert.NotEqual(Suit.Spades, state.Trump?.Suit);
            if (state.Penalty is { } penalty)
            {
                Assert.True(state.Players.Single(p => p.Id == penalty.ViolatorId).IsActive, "Карти віддають тому, хто вже вибув");
                Assert.NotEmpty(penalty.PendingGiverIds);
                Assert.All(penalty.PendingGiverIds, id => Assert.True(state.Players.Single(p => p.Id == id).IsActive));
            }
            if (state.SubPhase == InProgressPhase.Dealing) Assert.Null(state.Penalty);
        }

        // Один хід бота: віддати карту по поганій, якщо від когось на неї чекають, інакше звичайний хід.
        // foulChance — як часто бот навмисно порушує правила. Повертає false, якщо ходити не було чим (цього бути не повинно).
        private static bool BotMove(GameService game, Random rng, double foulChance)
        {
            if (game.State.Penalty is { } penalty)
            {
                var giver = game.State.Players.First(p => p.Id == penalty.PendingGiverIds[0]);
                var giverView = game.GetViews(Info).Single(v => v.ConnectionId == giver.ConnectionId).View;
                var suggested = giverView.Hand.Where(c => c.Suggested).ToList();
                Assert.NotEmpty(suggested);
                // Частіше за порадою, але віддати можна будь-яку
                var options = rng.NextDouble() < 0.7 ? suggested : giverView.Hand;
                var given = options[rng.Next(options.Count)];
                Assert.Null(game.GivingPenalty(giver.Id, new Card(given.Suit, (Rank)given.Rank)));
                return true;
            }

            var current = game.State.Players[game.State.CurrentPlayerIndex];
            var view = game.GetViews(Info).Single(v => v.ConnectionId == current.ConnectionId).View;
            bool foul = rng.NextDouble() < foulChance;

            if (view.CanDraw)
            {
                // Верхню карту перекладати обов'язково. При провтику бот або кладе її навмання, або забуває й тягне з колоди.
                var mustMoveTo = game.GetRequiredMoveTarget(current.Id);
                var others = view.Players.Where(p => p.Id != view.MyId).ToList();
                bool hasTop = view.Players.Single(p => p.Id == view.MyId).TopCard != null;
                if (foul && hasTop && rng.Next(2) == 0)
                {
                    Assert.Null(game.MovingTopCard(current.Id, others[rng.Next(others.Count)].Id));
                    return true;
                }
                if (mustMoveTo != null && !foul)
                {
                    Assert.Null(game.MovingTopCard(current.Id, mustMoveTo));
                    return true;
                }

                Assert.Null(game.Drawing(current.Id));
                return true;
            }
            if (view.DrawnCard != null)
            {
                var targets = game.GetLegalDealTargets(current.Id);
                var wrong = game.State.Players.Select(p => p.Id).Except(targets).ToList();
                var pool = foul && wrong.Count > 0 ? wrong : targets;
                Assert.Null(game.Dealing(current.Id, pool[rng.Next(pool.Count)]));
                return true;
            }

            var legal = game.GetLegalPuts(current.Id);
            var illegal = current.Hand.Except(legal).ToList();
            if (foul && illegal.Count > 0)
            {
                Assert.Null(game.Putting(current.Id, illegal[rng.Next(illegal.Count)]));
                Assert.Equal(GameActionKind.Foul, game.State.LastAction!.Kind);
                return true;
            }
            if (legal.Count > 0 && (!view.CanTake || rng.NextDouble() > 0.05))
            {
                // Переважно найменша карта, як і грають люди
                var ordered = legal.OrderBy(c => c.Rank).ToList();
                var pick = ordered[rng.NextDouble() < 0.7 ? 0 : rng.Next(ordered.Count)];
                Assert.Null(game.Putting(current.Id, pick));
                return true;
            }
            if (view.CanTake)
            {
                Assert.Null(game.Taking(current.Id));
                return true;
            }
            return false;
        }

        [Theory]
        [InlineData(2, 0)]
        [InlineData(3, 0)]
        [InlineData(4, 0)]
        [InlineData(5, 0)]
        [InlineData(6, 0)]
        [InlineData(2, 0.05)]
        [InlineData(3, 0.05)]
        [InlineData(4, 0.05)]
        [InlineData(5, 0.05)]
        [InlineData(6, 0.05)]
        [InlineData(3, 0.3)] // бот, який порушує постійно: карти ходять по колу, але партія все одно має закінчитись
        [InlineData(6, 0.3)]
        public void RandomGamesAlwaysFinishWithOneLoser(int playerCount, double foulChance)
        {
            var rng = new Random(12345 + playerCount);

            for (int gameNo = 0; gameNo < 300; gameNo++)
            {
                var game = new GameService(rng: new Random(rng.Next()));
                var players = Enumerable.Range(0, playerCount)
                    .Select(i => game.Join($"Бот{i}", $"conn{i}", out _)!)
                    .ToList();
                Assert.Null(game.StartGame(players[0].Id));

                int moves = 0;
                while (game.State.Phase == GamePhase.InProgress)
                {
                    Assert.True(BotMove(game, rng, foulChance), "Поточний гравець не має жодного можливого ходу");
                    AssertInvariants(game);
                    Assert.True(++moves < MaxMoves, $"Партія {gameNo} не закінчилась за {MaxMoves} ходів");
                }

                Assert.Equal(GamePhase.Finished, game.State.Phase);
                Assert.NotNull(game.State.LoserId);
                Assert.Equal(
                    Enumerable.Range(1, playerCount),
                    players.Select(p => p.Place!.Value).OrderBy(x => x));
                Assert.Equal(playerCount, players.Single(p => p.Id == game.State.LoserId).Place);
            }
        }

        // Те саме, але посеред гри гравці випадково виходять: стан не має ламатися
        [Theory]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(6)]
        public void GamesSurvivePlayersLeaving(int playerCount)
        {
            var rng = new Random(777 + playerCount);

            for (int gameNo = 0; gameNo < 200; gameNo++)
            {
                var game = new GameService(rng: new Random(rng.Next()));
                var players = Enumerable.Range(0, playerCount)
                    .Select(i => game.Join($"Бот{i}", $"conn{i}", out _)!)
                    .ToList();
                Assert.Null(game.StartGame(players[0].Id));

                int moves = 0;
                while (game.State.Phase == GamePhase.InProgress)
                {
                    if (rng.NextDouble() < 0.01)
                    {
                        var leaver = game.State.Players[rng.Next(game.State.Players.Count)];
                        Assert.True(game.Leave(leaver.Id));
                        continue;
                    }

                    Assert.True(BotMove(game, rng, foulChance: 0.05), "Поточний гравець не має жодного можливого ходу");
                    Assert.True(++moves < MaxMoves, $"Партія {gameNo} не закінчилась за {MaxMoves} ходів");

                    var state = game.State;
                    if (state.Phase == GamePhase.InProgress)
                    {
                        Assert.True(state.Players[state.CurrentPlayerIndex].IsActive);
                        if (state.SubPhase == InProgressPhase.Playing) Assert.NotEmpty(state.Players[state.CurrentPlayerIndex].Hand);
                    }
                }

                Assert.Equal(GamePhase.Finished, game.State.Phase);
            }
        }
    }
}
