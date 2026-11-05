using DurakBackend.DTOs;
using DurakBackend.Models;
using DurakBackend.Services;
using Xunit;
using static DurakBackend.Models.Rank;
using static DurakBackend.Models.Suit;

namespace CardGame.Tests
{
    public class BotTests
    {
        private static readonly RoomInfo Info = new("0000", "Боти", false);

        private static Card C(Suit suit, Rank rank) => new(suit, rank);

        // Стіл лише з ботів: людини-господаря тут немає, тож партію запускаємо напряму
        private static (GameService Game, List<Player> Bots) AllBots(int seed, params BotLevel[] levels)
        {
            var game = new GameService(levels.Length, new Random(seed));
            var bots = levels
                .Select((level, i) => new Player { Id = $"bot{i}", Token = $"token{i}", Nick = $"Бот {i}", BotLevel = level })
                .ToList();
            game.State.Players.AddRange(bots);
            game.State.HostId = bots[0].Id;
            Assert.Null(game.StartGame(bots[0].Id));
            return (game, bots);
        }

        // Людина-господар і боти за столом, гра ще не почалась
        private static (GameService Game, Player Human, List<Player> Bots) HumanWithBots(params BotLevel[] levels)
        {
            var game = new GameService(GameService.MaxPlayers, new Random(7));
            var human = game.Join("Петро", "conn", out _)!;
            foreach (var level in levels) Assert.Null(game.AddBot(human.Id, level));
            return (game, human, game.State.Players.Where(p => p.IsBot).ToList());
        }

        // Партія на 2 етапі: задані руки, пеньків немає, ходить players[current]
        private static void SetPlaying(GameService game, Suit trump, int current, params Card[][] hands)
        {
            var players = game.State.Players;
            for (int i = 0; i < hands.Length; i++)
            {
                players[i].Hand = hands[i].ToList();
                players[i].Penky.Clear();
            }
            game.State.Deck.Clear();
            game.State.Trump = C(trump, Six);
            game.State.SubPhase = InProgressPhase.Playing;
            game.State.CurrentPlayerIndex = current;
        }

        // ---------- Місце за столом ----------

        [Fact]
        public void HostAddsBotsWithDifferentNames()
        {
            var (game, _, bots) = HumanWithBots(BotLevel.Easy, BotLevel.Medium, BotLevel.Hard);

            Assert.Equal(3, bots.Count);
            Assert.Equal(3, bots.Select(b => b.Nick).Distinct().Count());
            Assert.All(bots, b => Assert.StartsWith("Бот ", b.Nick));
            Assert.Equal(new BotLevel?[] { BotLevel.Easy, BotLevel.Medium, BotLevel.Hard }, bots.Select(b => b.BotLevel));
            Assert.Equal(4, game.State.Players.Count);
        }

        [Fact]
        public void OnlyHostAddsBotsAndOnlyBeforeTheGame()
        {
            var (game, human, _) = HumanWithBots();
            var guest = game.Join("Гість", "guest", out _)!;

            Assert.NotNull(game.AddBot(guest.Id, BotLevel.Easy));

            Assert.Null(game.StartGame(human.Id));
            Assert.NotNull(game.AddBot(human.Id, BotLevel.Easy));
        }

        [Fact]
        public void BotsTakeSeatsAndFullTableRejectsMore()
        {
            var game = new GameService(2, new Random(1));
            var human = game.Join("Петро", "conn", out _)!;

            Assert.Null(game.AddBot(human.Id, BotLevel.Hard));
            Assert.NotNull(game.AddBot(human.Id, BotLevel.Hard));
            Assert.Null(game.Join("Гість", "guest", out _));
        }

        [Fact]
        public void BotIsNeverHostAndCanBeRemoved()
        {
            var (game, human, bots) = HumanWithBots(BotLevel.Medium);
            var guest = game.Join("Гість", "guest", out _)!;

            Assert.Null(game.CheckKick(human.Id, bots[0].Id));
            Assert.True(game.Leave(human.Id));

            Assert.Equal(guest.Id, game.State.HostId); // бот сидів раніше за гостя, але господарем не став
            Assert.True(game.Leave(bots[0].Id));
            Assert.DoesNotContain(bots[0], game.State.Players);
        }

        [Fact]
        public void RoomDisappearsWhenOnlyBotsRemain()
        {
            var rooms = new RoomManager();
            var created = rooms.Create("host", new CreateRoomRequest("Петро", null, 4, false));
            var seat = rooms.FindSeat("host")!;
            Assert.Null(seat.Room.Game.AddBot(seat.PlayerId, BotLevel.Easy));
            Assert.Null(seat.Room.Game.StartGame(seat.PlayerId));

            rooms.Leave("host");

            Assert.Empty(rooms.ListRooms());
            Assert.Empty(rooms.AllRooms());
            Assert.NotNull(rooms.Join("other", new JoinRoomRequest("Інший", created.Code!)).Error);
        }

        [Fact]
        public void AbandonedRoomWithBotsIsSweptAfterHumanTimesOut()
        {
            var rooms = new RoomManager();
            rooms.Create("host", new CreateRoomRequest("Петро", null, 4, false));
            var seat = rooms.FindSeat("host")!;
            Assert.Null(seat.Room.Game.AddBot(seat.PlayerId, BotLevel.Easy));
            Assert.Null(seat.Room.Game.StartGame(seat.PlayerId));
            var t = DateTime.UtcNow;
            rooms.MarkDisconnected("host", t);

            rooms.Sweep(t + GameService.GraceInGame, out var changed);

            Assert.True(changed);
            Assert.Empty(rooms.AllRooms());
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void BotSeatCannotBeTakenThroughRejoin(string? token)
        {
            var rooms = new RoomManager();
            var created = rooms.Create("host", new CreateRoomRequest("Петро", null, 4, false));
            var seat = rooms.FindSeat("host")!;
            Assert.Null(seat.Room.Game.AddBot(seat.PlayerId, BotLevel.Easy));
            var request = new RejoinRequest(created.Code!, token!);

            Assert.False(rooms.CanRejoin(request));
            Assert.NotNull(rooms.Rejoin("thief", request, out _, out _).Error);
        }

        [Fact]
        public void BotLooksOnlineAndGetsNoMessages()
        {
            var (game, human, bots) = HumanWithBots(BotLevel.Hard);

            var views = game.GetViews(Info);

            var view = Assert.Single(views).View;
            Assert.Equal(human.Id, view.MyId);
            var botView = view.Players.Single(p => p.Id == bots[0].Id);
            Assert.True(botView.IsOnline);
            Assert.Equal(BotLevel.Hard, botView.Bot);
            Assert.Null(view.Players.Single(p => p.Id == human.Id).Bot);
        }

        [Fact]
        public void BotTokenDoesNotReachClients()
        {
            var (game, _, bots) = HumanWithBots(BotLevel.Hard);

            var json = System.Text.Json.JsonSerializer.Serialize(game.GetViews(Info).Single().View);

            Assert.DoesNotContain(bots[0].Token, json);
        }

        // ---------- Коли бот ходить ----------

        [Fact]
        public void BotWaitsForHumanTurn()
        {
            var (game, human, _) = HumanWithBots(BotLevel.Medium);
            Assert.Null(game.StartGame(human.Id));
            SetPlaying(game, Clubs, 0, new[] { C(Hearts, Six) }, new[] { C(Hearts, Seven) });
            int version = game.State.Version;

            Assert.False(game.PlayBotMove());
            Assert.False(game.RunBots(DateTime.UtcNow.AddHours(1)));

            Assert.Equal(version, game.State.Version);
        }

        [Fact]
        public void BotThinksBeforeMoving()
        {
            var (game, human, bots) = HumanWithBots(BotLevel.Medium);
            Assert.Null(game.StartGame(human.Id));
            SetPlaying(game, Clubs, 1, new[] { C(Hearts, Six), C(Hearts, Eight) }, new[] { C(Hearts, Seven), C(Hearts, Nine) });
            var t = DateTime.UtcNow;

            Assert.False(game.RunBots(t));                      // починає думати
            Assert.False(game.RunBots(t.AddMilliseconds(100))); // ще думає
            Assert.True(game.RunBots(t.AddSeconds(5)));

            Assert.Equal(bots[0].Id, game.State.LastAction!.ActorId);
        }

        [Fact]
        public void BotsSpeedUpWhenNoHumanIsStillPlaying()
        {
            var (game, human, bots) = HumanWithBots(BotLevel.Medium, BotLevel.Medium);
            Assert.Null(game.StartGame(human.Id));
            SetPlaying(game, Clubs, 1,
                new[] { C(Hearts, Six) },
                new[] { C(Hearts, Seven), C(Hearts, Nine) },
                new[] { C(Diamonds, Seven), C(Diamonds, Nine) });
            human.Place = 1;
            var t = DateTime.UtcNow;

            Assert.False(game.RunBots(t));

            Assert.True(game.RunBots(t.AddMilliseconds(400)), "Коли людям лишилось лише чекати, бот не має думати довго");
            Assert.Equal(bots[0].Id, game.State.LastAction!.ActorId);
        }

        [Fact]
        public void BotsPlayDrawAndPlaceOnStageOne()
        {
            var (game, human, bots) = HumanWithBots(BotLevel.Hard);
            Assert.Null(game.StartGame(human.Id));
            human.Hand = new List<Card> { C(Clubs, Seven) };
            bots[0].Hand = new List<Card> { C(Hearts, Nine) };
            game.State.Deck = new List<Card> { C(Spades, Jack), C(Clubs, Eight) };
            game.State.CurrentPlayerIndex = 1;

            Assert.True(game.PlayBotMove());
            Assert.Equal(C(Clubs, Eight), game.State.DrawnCard);

            Assert.True(game.PlayBotMove());
            Assert.Equal(C(Clubs, Eight), human.Hand[^1]); // вісімка підходить на сімку людини
            Assert.Null(game.TakeBotError());
        }

        [Theory]
        [InlineData(BotLevel.Medium)]
        [InlineData(BotLevel.Hard)]
        public void BotMovesItsTopCardBeforeDrawing(BotLevel level)
        {
            var (game, human, bots) = HumanWithBots(level);
            Assert.Null(game.StartGame(human.Id));
            human.Hand = new List<Card> { C(Clubs, Seven) };
            bots[0].Hand = new List<Card> { C(Hearts, Six), C(Hearts, Eight) };
            game.State.Deck = new List<Card> { C(Spades, Jack), C(Spades, King) };
            game.State.CurrentPlayerIndex = 1;

            Assert.True(game.PlayBotMove());

            Assert.Equal(GameActionKind.Move, game.State.LastAction!.Kind);
            Assert.Equal(C(Hearts, Eight), human.Hand[^1]);
            Assert.Null(game.TakeBotError());
        }

        [Theory]
        [InlineData(BotLevel.Medium)]
        [InlineData(BotLevel.Hard)]
        public void BotGivesCardToFirstFittingFromTheLeft(BotLevel level)
        {
            var (game, human, bots) = HumanWithBots(level, BotLevel.Easy);
            var guest = game.Join("Гість", "guest", out _)!; // порядок за столом: людина, бот, легкий, гість
            Assert.Null(game.StartGame(human.Id));
            human.Hand = new List<Card> { C(Clubs, Seven) };
            bots[0].Hand = new List<Card> { C(Hearts, King), C(Hearts, Eight) };
            bots[1].Hand = new List<Card> { C(Spades, Seven) };
            guest.Hand = new List<Card> { C(Diamonds, Seven) };
            game.State.Deck = new List<Card> { C(Spades, Jack), C(Spades, King) };
            game.State.CurrentPlayerIndex = 1;

            Assert.True(game.PlayBotMove());

            Assert.Equal(C(Hearts, Eight), bots[1].Hand[^1]); // зліва від бота сидить наступний за ним
            Assert.Equal(0, bots[0].Fouls);
        }

        [Fact]
        public void MemoryFollowsMovedCard()
        {
            var (game, human, bots) = HumanWithBots(BotLevel.Hard);
            Assert.Null(game.StartGame(human.Id));
            human.Hand = new List<Card> { C(Clubs, Eight) };
            bots[0].Hand = new List<Card> { C(Hearts, Seven) };
            game.State.Deck = new List<Card> { C(Spades, Jack) };
            game.State.CurrentPlayerIndex = 0;

            Assert.Null(game.MovingTopCard(human.Id, bots[0].Id));

            Assert.Contains(C(Clubs, Eight), bots[0].Memory!.KnownHand(bots[0].Id));
            Assert.DoesNotContain(C(Clubs, Eight), bots[0].Memory!.KnownHand(human.Id));
        }

        [Fact]
        public void HardBotPlacesCardOnlyWhereItFits()
        {
            var (game, bots) = AllBots(3, BotLevel.Hard, BotLevel.Hard, BotLevel.Hard);
            for (int i = 0; i < 80 && game.State.SubPhase == InProgressPhase.Dealing; i++)
            {
                Assert.True(game.PlayBotMove());
                Assert.NotEqual(GameActionKind.Foul, game.State.LastAction!.Kind);
            }
            Assert.Equal(0, bots.Sum(b => b.Fouls));
        }

        [Fact]
        public void BotGivesCardWhenHumanFouls()
        {
            var (game, human, bots) = HumanWithBots(BotLevel.Medium);
            Assert.Null(game.StartGame(human.Id));
            SetPlaying(game, Clubs, 0,
                new[] { C(Diamonds, Ace), C(Diamonds, Seven) },
                new[] { C(Hearts, Nine), C(Hearts, King), C(Clubs, Ace) });
            game.State.Table.Cards.Add(C(Hearts, Eight));
            game.State.Table.RoundSize = 2;
            game.State.Table.RoundPuts = 1;

            Assert.Null(game.Putting(human.Id, C(Diamonds, Ace))); // бубна чирву не б'є
            Assert.Equal(new[] { bots[0].Id }, game.State.Penalty!.PendingGiverIds);

            Assert.True(game.PlayBotMove());

            Assert.Null(game.State.Penalty);
            Assert.Contains(C(Hearts, Nine), human.Hand); // середній віддає найменшу некозирну
            Assert.Equal(GameActionKind.PenaltyGive, game.State.LastAction!.Kind);
        }

        [Fact]
        public void BotWaitsForHumanWhoStillOwesCardAndGivesItsOwnFirst()
        {
            var (game, human, bots) = HumanWithBots(BotLevel.Easy, BotLevel.Easy);
            Assert.Null(game.StartGame(human.Id));
            SetPlaying(game, Clubs, 1,
                new[] { C(Spades, Six), C(Spades, Seven) },
                new[] { C(Hearts, Nine) },
                new[] { C(Diamonds, Nine), C(Diamonds, Ten) });
            bots[0].Fouls = 0;
            game.State.Penalty = new PenaltyState(bots[0].Id, new List<string> { human.Id, bots[1].Id });

            Assert.True(game.PlayBotMove());
            Assert.Equal(new[] { human.Id }, game.State.Penalty!.PendingGiverIds);

            Assert.False(game.PlayBotMove()); // порушник чекає на людину, ходити не можна
            Assert.NotNull(game.State.Penalty);
        }

        // ---------- Чесність ----------

        [Fact]
        public void HardBotDecisionDoesNotDependOnHiddenCards()
        {
            GameAction Decide(bool swapHidden)
            {
                var (game, human, bots) = HumanWithBots(BotLevel.Hard);
                Assert.Null(game.StartGame(human.Id));
                SetPlaying(game, Clubs, 1,
                    new[] { C(Hearts, Six), C(Spades, Queen) },
                    new[] { C(Hearts, Seven), C(Hearts, Ace), C(Diamonds, Six), C(Clubs, Ten) });
                human.Penky = new List<Card> { C(Spades, Six), C(Spades, Seven) };
                bots[0].Penky = new List<Card> { C(Clubs, Ace), C(Diamonds, Ace) };
                if (swapHidden) (human.Penky, bots[0].Penky) = (bots[0].Penky, human.Penky);

                game.State.Table.Cards.Add(C(Hearts, Six));
                human.Hand.Remove(C(Hearts, Six));
                game.State.Table.RoundSize = 2;
                game.State.Table.RoundPuts = 1;

                Assert.True(game.PlayBotMove());
                return game.State.LastAction! with { Seq = 0 };
            }

            Assert.Equal(Decide(false).Card, Decide(true).Card);
            Assert.Equal(Decide(false).Kind, Decide(true).Kind);
        }

        [Fact]
        public void MemoryKeepsOpenCardsButNotClosedOnes()
        {
            var game = new GameService(GameService.MaxPlayers, new Random(5));
            var host = game.Join("Петро", "c1", out _)!;
            var guest = game.Join("Оля", "c2", out _)!;
            Assert.Null(game.AddBot(host.Id, BotLevel.Hard));
            var bot = game.State.Players.Single(p => p.IsBot);
            Assert.Null(game.StartGame(host.Id));
            var memory = bot.Memory!;
            Assert.Equal(host.Hand[0], Assert.Single(memory.KnownHand(host.Id)));

            SetPlaying(game, Clubs, 0,
                new[] { C(Hearts, Six), C(Hearts, Ace), C(Diamonds, Ace) },
                new[] { C(Spades, Six), C(Spades, Seven) },
                new[] { C(Clubs, Six), C(Clubs, Seven) });
            var before = memory.KnownHand(guest.Id).Count;

            // Людина походила не за правилами: кожен дає їй карту, але бот бачить лише свою
            Assert.Null(game.Putting(host.Id, C(Hearts, Six)));
            game.State.CurrentPlayerIndex = 1;
            Assert.Null(game.Putting(guest.Id, C(Spades, Six))); // піка чирву не б'є
            Assert.Null(game.GivingPenalty(host.Id, C(Hearts, Ace)));

            Assert.Equal(GameActionKind.PenaltyGive, game.State.LastAction!.Kind);
            Assert.DoesNotContain(C(Hearts, Ace), memory.KnownHand(guest.Id));
            Assert.Equal(before + 1, memory.KnownHand(guest.Id).Count); // лише піка, якою спробували походити
            Assert.Contains(C(Spades, Six), memory.KnownHand(guest.Id));
        }

        [Fact]
        public void MemoryTracksTakenAndDiscardedCards()
        {
            var (game, human, bots) = HumanWithBots(BotLevel.Hard, BotLevel.Hard);
            Assert.Null(game.StartGame(human.Id));
            var memory = bots[0].Memory!;
            SetPlaying(game, Clubs, 0,
                new[] { C(Hearts, Six), C(Spades, Six) },
                new[] { C(Diamonds, Six), C(Diamonds, Seven) },
                new[] { C(Clubs, Seven), C(Clubs, Eight) });

            Assert.Null(game.Putting(human.Id, C(Hearts, Six)));
            Assert.Null(game.Taking(bots[0].Id));

            Assert.Contains(C(Hearts, Six), memory.KnownHand(bots[0].Id));

            game.State.CurrentPlayerIndex = 2;
            Assert.Null(game.Putting(bots[1].Id, C(Clubs, Seven)));
            game.State.CurrentPlayerIndex = 0;
            Assert.Null(game.Putting(human.Id, C(Spades, Six))); // провтик: тепер усі знають про цю карту

            Assert.Contains(C(Spades, Six), memory.KnownHand(human.Id));
        }

        // ---------- Цілі партії ----------

        [Theory]
        [InlineData(BotLevel.Easy, BotLevel.Easy)]
        [InlineData(BotLevel.Medium, BotLevel.Medium)]
        [InlineData(BotLevel.Hard, BotLevel.Hard)]
        [InlineData(BotLevel.Hard, BotLevel.Hard, BotLevel.Hard)]
        [InlineData(BotLevel.Easy, BotLevel.Medium, BotLevel.Hard)]
        [InlineData(BotLevel.Hard, BotLevel.Hard, BotLevel.Hard, BotLevel.Hard, BotLevel.Hard)]
        [InlineData(BotLevel.Easy, BotLevel.Medium, BotLevel.Hard, BotLevel.Easy, BotLevel.Medium, BotLevel.Hard)]
        [InlineData(BotLevel.Hard, BotLevel.Hard, BotLevel.Hard, BotLevel.Hard, BotLevel.Hard, BotLevel.Hard)]
        public void BotGamesAlwaysFinishWithoutLosingCards(params BotLevel[] levels)
        {
            for (int seed = 0; seed < 60; seed++)
            {
                var (game, bots) = AllBots(seed, levels);

                int moves = 0;
                while (game.State.Phase == GamePhase.InProgress)
                {
                    Assert.True(game.PlayBotMove(), "Партія завмерла: ніхто не може ходити");
                    Assert.Null(game.TakeBotError());
                    Assert.True(++moves < 2000, "Партія не закінчилась за 2000 ходів");

                    var state = game.State;
                    Assert.Equal(36, state.Deck.Count
                        + state.Table.Cards.Count
                        + state.DiscardCount
                        + (state.DrawnCard == null ? 0 : 1)
                        + state.Players.Sum(p => p.Hand.Count + p.Penky.Count));
                }

                Assert.NotNull(game.State.LoserId);
                Assert.Equal(Enumerable.Range(1, levels.Length), bots.Select(b => b.Place!.Value).OrderBy(x => x));
            }
        }

        [Fact]
        public void BotsPlayRematchWithFreshMemory()
        {
            var (game, bots) = AllBots(11, BotLevel.Hard, BotLevel.Medium);
            while (game.State.Phase == GamePhase.InProgress) game.PlayBotMove();
            var oldMemory = bots[0].Memory;

            Assert.Null(game.StartGame(bots[0].Id));

            Assert.NotSame(oldMemory, bots[0].Memory);
            Assert.Empty(bots[0].Memory!.Discarded);
            Assert.True(game.PlayBotMove());
        }

        // ---------- Сила рівнів ----------

        // Скільки партій програв кожен рівень, коли сидить за столом на всіх місцях по черзі
        private static Dictionary<BotLevel, double> LossRates(int games, params BotLevel[] seats)
        {
            var losses = seats.Distinct().ToDictionary(level => level, _ => 0);
            var played = seats.Distinct().ToDictionary(level => level, _ => 0);

            for (int g = 0; g < games; g++)
            {
                var rotated = seats.Select((_, i) => seats[(i + g) % seats.Length]).ToArray();
                var (game, bots) = AllBots(2000 + g, rotated);
                while (game.State.Phase == GamePhase.InProgress) game.PlayBotMove();

                foreach (var bot in bots) played[bot.BotLevel!.Value]++;
                losses[bots.Single(b => b.Id == game.State.LoserId).BotLevel!.Value]++;
            }
            return losses.ToDictionary(k => k.Key, k => k.Value / (double)played[k.Key] * seats.Count(s => s == k.Key));
        }

        [Fact]
        public void EachLevelClearlyBeatsThePreviousOne()
        {
            Assert.True(LossRates(600, BotLevel.Easy, BotLevel.Medium)[BotLevel.Easy] > 0.7, "Легкий мав би програвати середньому");
            Assert.True(LossRates(600, BotLevel.Medium, BotLevel.Hard)[BotLevel.Medium] > 0.7, "Середній мав би програвати складному");
            Assert.True(LossRates(600, BotLevel.Easy, BotLevel.Hard)[BotLevel.Easy] > 0.8, "Легкий мав би програвати складному ще частіше");
        }

        [Fact]
        public void LevelsKeepTheirOrderAtALargerTable()
        {
            var rates = LossRates(400, BotLevel.Easy, BotLevel.Medium, BotLevel.Hard);

            Assert.True(rates[BotLevel.Easy] > rates[BotLevel.Medium], "Легкий мав би програвати частіше за середнього");
            Assert.True(rates[BotLevel.Medium] > rates[BotLevel.Hard], "Середній мав би програвати частіше за складного");
        }

        [Fact]
        public void OnlyWeakerLevelsMakeFouls()
        {
            var fouls = new Dictionary<BotLevel, int> { [BotLevel.Easy] = 0, [BotLevel.Medium] = 0, [BotLevel.Hard] = 0 };

            for (int seed = 0; seed < 150; seed++)
            {
                var (game, bots) = AllBots(seed, BotLevel.Easy, BotLevel.Medium, BotLevel.Hard);
                while (game.State.Phase == GamePhase.InProgress)
                {
                    game.PlayBotMove();
                    if (game.State.LastAction!.Kind == GameActionKind.Foul)
                    {
                        fouls[bots.Single(b => b.Id == game.State.LastAction.ActorId).BotLevel!.Value]++;
                    }
                }
            }

            Assert.Equal(0, fouls[BotLevel.Hard]);
            Assert.True(fouls[BotLevel.Medium] > 0, "Середній інколи помиляється");
            Assert.True(fouls[BotLevel.Easy] > fouls[BotLevel.Medium] * 2, "Легкий помиляється значно частіше за середнього");
        }
    }
}
