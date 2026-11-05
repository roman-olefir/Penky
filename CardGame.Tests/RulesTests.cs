using DurakBackend.Models;
using DurakBackend.Services;
using Xunit;
using static DurakBackend.Models.Rank;
using static DurakBackend.Models.Suit;

namespace CardGame.Tests
{
    public class RulesTests
    {
        private static Card C(Suit suit, Rank rank) => new(suit, rank);

        // Стіл із гравцями a, b, c...; перший гравець — господар
        private static (GameService Game, List<Player> Players) NewTable(int count)
        {
            var game = new GameService(rng: new Random(1));
            var players = Enumerable.Range(0, count)
                .Select(i => game.Join($"Гравець{i}", $"conn{i}", out _)!)
                .ToList();
            return (game, players);
        }

        // Партія на 1 етапі: у кожного в купці одна задана карта, ходить players[0]
        private static (GameService Game, List<Player> Players) Dealing(Card[] piles, params Card[] deckFromBottom)
        {
            var (game, players) = NewTable(piles.Length);
            Assert.Null(game.StartGame(players[0].Id));
            for (int i = 0; i < piles.Length; i++) players[i].Hand = new List<Card> { piles[i] };
            game.State.Deck = deckFromBottom.ToList();
            game.State.CurrentPlayerIndex = 0;
            return (game, players);
        }

        // Партія на 2 етапі: задані руки, пеньків немає, ходить players[0]
        private static (GameService Game, List<Player> Players) Playing(Suit trump, params Card[][] hands)
        {
            var (game, players) = NewTable(hands.Length);
            Assert.Null(game.StartGame(players[0].Id));
            for (int i = 0; i < hands.Length; i++)
            {
                players[i].Hand = hands[i].ToList();
                players[i].Penky.Clear();
            }
            game.State.Deck.Clear();
            game.State.Trump = C(trump, Six);
            game.State.SubPhase = InProgressPhase.Playing;
            game.State.CurrentPlayerIndex = 0;
            return (game, players);
        }

        private static string CurrentId(GameService game) => game.State.Players[game.State.CurrentPlayerIndex].Id;

        // ---------- Початок гри ----------

        [Theory]
        [InlineData(2)]
        [InlineData(6)]
        public void StartDealsTwoPenkyAndOneOpenCard(int count)
        {
            var (game, players) = NewTable(count);

            Assert.Null(game.StartGame(players[0].Id));

            Assert.All(players, p =>
            {
                Assert.Equal(2, p.Penky.Count);
                Assert.Single(p.Hand);
            });
            Assert.Equal(36 - count * 3, game.State.Deck.Count);
            Assert.Equal(InProgressPhase.Dealing, game.State.SubPhase);
        }

        [Fact]
        public void OnlyHostCanStart()
        {
            var (game, players) = NewTable(2);
            Assert.NotNull(game.StartGame(players[1].Id));
            Assert.Equal(GamePhase.WaitingForPlayers, game.State.Phase);
        }

        [Fact]
        public void CannotStartAlone()
        {
            var (game, players) = NewTable(1);
            Assert.NotNull(game.StartGame(players[0].Id));
        }

        // ---------- 1 етап ----------

        [Fact]
        public void CannotPlaceBeforeDrawing()
        {
            var (game, p) = Dealing(new[] { C(Clubs, Seven), C(Hearts, Nine) }, C(Spades, Ten), C(Clubs, Ten));
            Assert.NotNull(game.Dealing(p[0].Id, p[1].Id));
        }

        [Fact]
        public void CannotDrawOutOfTurn()
        {
            var (game, p) = Dealing(new[] { C(Clubs, Seven), C(Hearts, Nine) }, C(Spades, Ten), C(Clubs, Ten));
            Assert.NotNull(game.Drawing(p[1].Id));
            Assert.Null(game.State.DrawnCard);
        }

        [Fact]
        public void CardOneRankHigherGoesOnOpponentAndTurnStays()
        {
            var (game, p) = Dealing(new[] { C(Clubs, Seven), C(Hearts, Nine) }, C(Spades, Jack), C(Clubs, Ten));

            Assert.Null(game.Drawing(p[0].Id));
            Assert.Null(game.Dealing(p[0].Id, p[1].Id));

            Assert.Equal(C(Clubs, Ten), p[1].Hand[^1]);
            Assert.Equal(p[0].Id, CurrentId(game));
        }

        [Fact]
        public void SixGoesOnAce()
        {
            var (game, p) = Dealing(new[] { C(Clubs, Seven), C(Hearts, Ace) }, C(Spades, Jack), C(Clubs, Six));

            Assert.Null(game.Drawing(p[0].Id));
            Assert.Null(game.Dealing(p[0].Id, p[1].Id));

            Assert.Equal(C(Clubs, Six), p[1].Hand[^1]);
        }

        [Fact]
        public void KeepingCardThatFitsOpponentIsCountedAsFoul()
        {
            var (game, p) = Dealing(new[] { C(Clubs, Seven), C(Hearts, Nine) }, C(Spades, Jack), C(Clubs, Ten));

            Assert.Null(game.Drawing(p[0].Id));
            Assert.Null(game.Dealing(p[0].Id, p[0].Id));

            Assert.Single(p[0].Hand);
            Assert.Equal(C(Clubs, Ten), game.State.DrawnCard);
            Assert.Equal(GameActionKind.Foul, game.State.LastAction!.Kind);
            Assert.Equal(1, p[0].Fouls);
            Assert.Null(game.State.Penalty); // карти віддають лише на 2 етапі
            Assert.Equal(p[0].Id, CurrentId(game));
        }

        [Fact]
        public void GivingCardToOpponentItDoesNotFitIsCountedAsFoul()
        {
            var (game, p) = Dealing(new[] { C(Clubs, Seven), C(Hearts, Nine) }, C(Spades, Jack), C(Clubs, King));

            Assert.Null(game.Drawing(p[0].Id));
            Assert.Null(game.Dealing(p[0].Id, p[1].Id));

            Assert.Single(p[1].Hand);
            Assert.Equal(C(Clubs, King), game.State.DrawnCard);
            Assert.Equal(1, p[0].Fouls);
            Assert.Null(game.State.Penalty);
        }

        [Fact]
        public void EveryWrongPlacementAddsAFoulUntilCardLandsCorrectly()
        {
            var (game, p) = Dealing(new[] { C(Clubs, Seven), C(Hearts, Nine) }, C(Spades, Jack), C(Clubs, King));
            Assert.Null(game.Drawing(p[0].Id));

            Assert.Null(game.Dealing(p[0].Id, p[1].Id));
            Assert.Null(game.Dealing(p[0].Id, p[1].Id));
            Assert.Null(game.Dealing(p[0].Id, p[0].Id)); // нікому не підходить, тож собі: це вже правильно

            Assert.Equal(2, p[0].Fouls);
            Assert.Equal(C(Clubs, King), p[0].Hand[^1]);
            Assert.Null(game.State.DrawnCard);
        }

        // Партія, де останню карту колоди кладе players[0], а провтики вже пораховані
        private static (GameService Game, List<Player> Players) LastDealWithFouls(params int[] fouls)
        {
            var piles = Enumerable.Range(0, fouls.Length).Select(_ => C(Hearts, Nine)).ToArray();
            var (game, p) = Dealing(piles, C(Diamonds, King));
            for (int i = 0; i < p.Count; i++)
            {
                p[i].Hand = new List<Card> { C(Spades, Ace), C(Clubs, Ace), C(Hearts, Ace) };
                p[i].Fouls = fouls[i];
            }
            Assert.Null(game.Drawing(p[0].Id));
            Assert.Null(game.Dealing(p[0].Id, p[0].Id));
            Assert.Equal(InProgressPhase.Playing, game.State.SubPhase);
            return (game, p);
        }

        [Fact]
        public void NoFoulsMeansNoPenaltyAtStageTwo()
        {
            var (game, _) = LastDealWithFouls(0, 0, 0);
            Assert.Null(game.State.Penalty);
        }

        [Fact]
        public void FoulsArePaidAtStageTwoStartOneCardPerFoulFromEachOtherPlayer()
        {
            var (game, p) = LastDealWithFouls(0, 2, 0);

            Assert.Equal(p[1].Id, game.State.Penalty!.ViolatorId);
            Assert.Equal(new[] { p[0].Id, p[0].Id, p[2].Id, p[2].Id }, game.State.Penalty.PendingGiverIds);
            int before = p[1].Hand.Count;

            Assert.Null(game.GivingPenalty(p[0].Id, C(Spades, Ace)));
            Assert.Null(game.GivingPenalty(p[0].Id, C(Clubs, Ace)));
            Assert.NotNull(game.GivingPenalty(p[0].Id, C(Hearts, Ace))); // більше двох не винен
            Assert.Null(game.GivingPenalty(p[2].Id, C(Spades, Ace)));
            Assert.NotNull(game.State.Penalty);
            Assert.Null(game.GivingPenalty(p[2].Id, C(Hearts, Ace)));

            Assert.Null(game.State.Penalty);
            Assert.Equal(before + 4, p[1].Hand.Count);
            Assert.Equal(0, p[1].Fouls);
        }

        [Fact]
        public void NoMovesUntilStageOneFoulsArePaid()
        {
            var (game, p) = LastDealWithFouls(0, 1, 0);

            Assert.NotNull(game.Putting(p[0].Id, p[0].Hand[0]));
            Assert.Empty(game.State.Table.Cards);

            Assert.Null(game.GivingPenalty(p[0].Id, C(Spades, Ace)));
            Assert.Null(game.GivingPenalty(p[2].Id, C(Spades, Ace)));

            Assert.Null(game.Putting(p[0].Id, p[0].Hand[0]));
            Assert.Single(game.State.Table.Cards);
        }

        [Fact]
        public void SeveralViolatorsArePaidInTurnStartingFromWhoeverMoves()
        {
            var (game, p) = LastDealWithFouls(1, 1, 0);

            Assert.Equal(p[0].Id, game.State.Penalty!.ViolatorId);
            Assert.Null(game.GivingPenalty(p[1].Id, C(Spades, Ace)));
            Assert.Null(game.GivingPenalty(p[2].Id, C(Spades, Ace)));

            // Перший порушник отримав своє і тепер сам дає другому
            Assert.Equal(p[1].Id, game.State.Penalty!.ViolatorId);
            Assert.Equal(new[] { p[0].Id, p[2].Id }, game.State.Penalty.PendingGiverIds);
        }

        [Fact]
        public void FirstMoverWhoGaveAwayAllCardsPassesTheTurn()
        {
            var (game, p) = LastDealWithFouls(0, 2, 0);
            p[0].Hand = new List<Card> { C(Spades, Six) };
            p[0].Penky.Clear();

            Assert.Null(game.GivingPenalty(p[0].Id, C(Spades, Six)));

            Assert.Equal(1, p[0].Place);
            Assert.Equal(new[] { p[2].Id, p[2].Id }, game.State.Penalty!.PendingGiverIds); // другу карту першому вже нічим віддати
            Assert.Equal(p[1].Id, CurrentId(game));
        }

        [Fact]
        public void ViolatorLeavingBeforePaymentMovesOnToNextViolator()
        {
            var (game, p) = LastDealWithFouls(1, 1, 0);

            Assert.True(game.Leave(p[0].Id));

            Assert.Equal(p[1].Id, game.State.Penalty!.ViolatorId);
            Assert.Equal(new[] { p[2].Id }, game.State.Penalty.PendingGiverIds);
        }

        [Fact]
        public void EveryoneSeesTheFoulCount()
        {
            var (game, p) = Dealing(new[] { C(Clubs, Seven), C(Hearts, Nine) }, C(Spades, Jack), C(Clubs, King));
            Assert.Null(game.Drawing(p[0].Id));
            Assert.Null(game.Dealing(p[0].Id, p[1].Id));

            var view = game.GetViews(new RoomInfo("1234", "Тест", false)).Single(v => v.ConnectionId == p[1].ConnectionId).View;

            Assert.Equal(1, view.Players[0].Fouls);
            Assert.Equal(0, view.Players[1].Fouls);
            Assert.Empty(view.Hand);
        }

        [Fact]
        public void CardThatFitsNobodyGoesToSelfAndTurnPasses()
        {
            var (game, p) = Dealing(new[] { C(Clubs, Seven), C(Hearts, Nine) }, C(Spades, Jack), C(Clubs, King));

            Assert.Null(game.Drawing(p[0].Id));
            Assert.Null(game.Dealing(p[0].Id, p[0].Id));

            Assert.Equal(C(Clubs, King), p[0].Hand[^1]);
            Assert.Equal(p[1].Id, CurrentId(game));
        }

        [Fact]
        public void CardThatFitsOwnPileKeepsTheTurn()
        {
            var (game, p) = Dealing(new[] { C(Clubs, Seven), C(Hearts, Nine) }, C(Spades, Jack), C(Clubs, Eight));

            Assert.Null(game.Drawing(p[0].Id));
            Assert.Null(game.Dealing(p[0].Id, p[0].Id));

            Assert.Equal(p[0].Id, CurrentId(game));
        }

        [Fact]
        public void LastCardSetsTrumpAndItsOwnerLeads()
        {
            var (game, p) = Dealing(new[] { C(Clubs, Seven), C(Hearts, Nine) }, C(Diamonds, King));

            Assert.Null(game.Drawing(p[0].Id));
            Assert.Null(game.Dealing(p[0].Id, p[0].Id));

            Assert.Equal(C(Diamonds, King), game.State.Trump);
            Assert.Equal(InProgressPhase.Playing, game.State.SubPhase);
            Assert.Equal(p[0].Id, CurrentId(game));
        }

        // ---------- 1 етап: перекладання верхньої карти ----------

        [Fact]
        public void TopCardMovesOnOpponentAndNextOneCanFollow()
        {
            var (game, p) = Dealing(new[] { C(Clubs, Eight), C(Hearts, Seven), C(Spades, Six) }, C(Spades, Jack));
            p[0].Hand = new List<Card> { C(Diamonds, Seven), C(Clubs, Eight) };

            Assert.Null(game.MovingTopCard(p[0].Id, p[1].Id)); // вісімка на сімку
            Assert.Null(game.MovingTopCard(p[0].Id, p[2].Id)); // сімка під нею на шістку

            Assert.Equal(C(Clubs, Eight), p[1].Hand[^1]);
            Assert.Equal(C(Diamonds, Seven), p[2].Hand[^1]);
            Assert.Empty(p[0].Hand);
            Assert.Equal(0, p[0].Fouls);
            Assert.Equal(p[0].Id, CurrentId(game));
            Assert.Equal(GameActionKind.Move, game.State.LastAction!.Kind);
        }

        [Fact]
        public void MovingOnOpponentItDoesNotFitIsFoulAndCardStays()
        {
            var (game, p) = Dealing(new[] { C(Clubs, Eight), C(Hearts, King) }, C(Spades, Jack));

            Assert.Null(game.MovingTopCard(p[0].Id, p[1].Id));

            Assert.Equal(1, p[0].Fouls);
            Assert.Equal(new[] { C(Clubs, Eight) }, p[0].Hand);
            Assert.Equal(new[] { C(Hearts, King) }, p[1].Hand);
            Assert.Equal(GameActionKind.Foul, game.State.LastAction!.Kind);
            Assert.Equal(p[0].Id, CurrentId(game));
            Assert.Contains("К♥", game.State.LastAction.Reason);
        }

        [Fact]
        public void CannotMoveAfterDrawingOutOfTurnOrOntoYourself()
        {
            var (game, p) = Dealing(new[] { C(Clubs, Eight), C(Hearts, King) }, C(Spades, Jack), C(Spades, King));

            Assert.NotNull(game.MovingTopCard(p[1].Id, p[0].Id)); // не свій хід
            Assert.NotNull(game.MovingTopCard(p[0].Id, p[0].Id)); // на себе
            Assert.Null(game.Drawing(p[0].Id));
            Assert.NotNull(game.MovingTopCard(p[0].Id, p[1].Id)); // уже витягнув

            Assert.Equal(0, p[0].Fouls);
            Assert.Single(p[0].Hand);
        }

        [Fact]
        public void DrawingWhenTopCardMustBeMovedIsFoulAndNothingIsDrawn()
        {
            var (game, p) = Dealing(new[] { C(Clubs, Eight), C(Hearts, Seven) }, C(Spades, Jack));

            Assert.Null(game.Drawing(p[0].Id));

            Assert.Equal(1, p[0].Fouls);
            Assert.Null(game.State.DrawnCard);
            Assert.Single(game.State.Deck);
            Assert.Equal(GameActionKind.Foul, game.State.LastAction!.Kind);
            Assert.Equal(p[0].Id, CurrentId(game));

            Assert.Null(game.MovingTopCard(p[0].Id, p[1].Id));
            Assert.Null(game.Drawing(p[0].Id));
            Assert.Equal(1, p[0].Fouls); // тепер перекладати нічого, тож витягнути можна
            Assert.NotNull(game.State.DrawnCard);
        }

        [Fact]
        public void TopCardMustGoToFirstFittingOpponentFromTheLeft()
        {
            var (game, p) = Dealing(new[] { C(Clubs, Eight), C(Hearts, King), C(Hearts, Seven), C(Spades, Seven) }, C(Spades, Jack));

            Assert.Null(game.MovingTopCard(p[0].Id, p[3].Id)); // підходить, але перший зліва — третій гравець
            Assert.Equal(1, p[0].Fouls);
            Assert.Equal(C(Clubs, Eight), p[0].Hand[^1]);

            Assert.Null(game.MovingTopCard(p[0].Id, p[2].Id));
            Assert.Equal(C(Clubs, Eight), p[2].Hand[^1]);
            Assert.Equal(1, p[0].Fouls);
        }

        [Fact]
        public void DrawnCardMustGoToFirstFittingOpponentFromTheLeft()
        {
            var (game, p) = Dealing(new[] { C(Clubs, King), C(Hearts, King), C(Hearts, Seven), C(Spades, Seven) }, C(Spades, Jack), C(Clubs, Eight));
            Assert.Null(game.Drawing(p[0].Id));

            Assert.Null(game.Dealing(p[0].Id, p[3].Id));
            Assert.Equal(1, p[0].Fouls);
            Assert.Single(p[3].Hand);

            Assert.Null(game.Dealing(p[0].Id, p[2].Id));
            Assert.Equal(C(Clubs, Eight), p[2].Hand[^1]);
        }

        [Fact]
        public void LeftToRightStartsFromTheNextPlayerAndWrapsAround()
        {
            var (game, p) = Dealing(new[] { C(Clubs, Seven), C(Hearts, King), C(Hearts, Eight), C(Spades, Seven) }, C(Spades, Jack), C(Clubs, Eight));
            game.State.CurrentPlayerIndex = 2; // для третього зліва сидять четвертий, перший, другий
            p[2].Hand = new List<Card> { C(Hearts, Eight) };

            Assert.Equal(p[3].Id, game.GetRequiredMoveTarget(p[2].Id));
            Assert.Null(game.MovingTopCard(p[2].Id, p[3].Id));
            Assert.Equal(0, p[2].Fouls);
        }

        [Fact]
        public void CardCannotBeMovedOnStageTwo()
        {
            var (game, p) = Playing(Clubs, new[] { C(Hearts, Eight) }, new[] { C(Hearts, Seven) });
            Assert.NotNull(game.MovingTopCard(p[0].Id, p[1].Id));
        }

        [Fact]
        public void MovedCardDoesNotAffectTrump()
        {
            var (game, p) = Dealing(new[] { C(Clubs, Eight), C(Hearts, Seven) }, C(Diamonds, King));

            Assert.Null(game.MovingTopCard(p[0].Id, p[1].Id));
            Assert.Null(game.Drawing(p[0].Id));
            Assert.Null(game.Dealing(p[0].Id, p[0].Id));

            Assert.Equal(C(Diamonds, King), game.State.Trump);
        }

        [Fact]
        public void EmptyPileCanStillDrawAndDrawnCardGoesToSelf()
        {
            var (game, p) = Dealing(new[] { C(Clubs, Eight), C(Hearts, Seven) }, C(Spades, Jack), C(Spades, King));

            Assert.Null(game.MovingTopCard(p[0].Id, p[1].Id));
            Assert.NotNull(game.MovingTopCard(p[0].Id, p[1].Id)); // перекладати вже нічого, але це не провтик
            Assert.Equal(0, p[0].Fouls);

            Assert.Null(game.Drawing(p[0].Id));
            Assert.Null(game.Dealing(p[0].Id, p[0].Id));

            Assert.Equal(new[] { C(Spades, King) }, p[0].Hand);
            Assert.Equal(p[1].Id, CurrentId(game));
        }

        [Fact]
        public void PlacingCardOnEmptyPileIsFoul()
        {
            var (game, p) = Dealing(new[] { C(Clubs, Eight), C(Hearts, Seven) }, C(Spades, Jack), C(Diamonds, King));
            p[0].Hand.Clear();
            game.State.CurrentPlayerIndex = 1;

            Assert.Null(game.Drawing(p[1].Id));
            Assert.Null(game.Dealing(p[1].Id, p[0].Id)); // на порожню купку класти не можна

            Assert.Equal(1, p[1].Fouls);
            Assert.Empty(p[0].Hand);
            Assert.Contains("порожня", game.State.LastAction!.Reason);
        }

        [Fact]
        public void PlayerWithoutPileStartsStageTwoWithPenky()
        {
            var (game, p) = Dealing(new[] { C(Clubs, Eight), C(Hearts, Seven) }, C(Diamonds, King));
            p[0].Penky = new List<Card> { C(Spades, Six), C(Spades, Seven) };
            game.State.CurrentPlayerIndex = 1;
            p[0].Hand.Clear();

            Assert.Null(game.Drawing(p[1].Id));
            Assert.Null(game.Dealing(p[1].Id, p[1].Id));

            Assert.Equal(InProgressPhase.Playing, game.State.SubPhase);
            Assert.Equal(2, p[0].Hand.Count);
            Assert.Empty(p[0].Penky);
        }

        [Fact]
        public void SpadeIsNeverTrumpSoEarlierNonSpadeDecides()
        {
            var (game, p) = Dealing(new[] { C(Clubs, Seven), C(Hearts, Nine) },
                C(Spades, King), C(Spades, Ace), C(Diamonds, Nine)); // тягнуть з кінця: спершу бубна, потім дві піки

            for (int i = 0; i < 3; i++)
            {
                var current = game.State.Players[game.State.CurrentPlayerIndex];
                Assert.Null(game.Drawing(current.Id));
                Assert.Null(game.Dealing(current.Id, game.GetLegalDealTargets(current.Id)[0]));
            }

            Assert.Equal(InProgressPhase.Playing, game.State.SubPhase);
            Assert.Equal(C(Diamonds, Nine), game.State.Trump);
        }

        // ---------- 2 етап: чим можна бити ----------

        [Theory]
        [InlineData(Hearts, King, Hearts, Ace, Clubs, true)]    // старша тієї самої масті
        [InlineData(Hearts, King, Hearts, Queen, Clubs, false)] // молодша тієї самої масті
        [InlineData(Hearts, King, Clubs, Ace, Diamonds, false)] // інша масть, не козир
        [InlineData(Hearts, Ace, Clubs, Six, Clubs, true)]      // козир б'є некозирну
        [InlineData(Clubs, Nine, Clubs, Seven, Clubs, false)]   // молодший козир не б'є старшого
        [InlineData(Clubs, Seven, Clubs, Nine, Clubs, true)]    // старший козир б'є молодшого
        [InlineData(Hearts, Ace, Hearts, Six, Clubs, true)]     // шістка б'є туза тієї самої масті
        [InlineData(Hearts, Ace, Clubs, Six, Diamonds, false)]  // шістка іншої масті туза не б'є
        [InlineData(Spades, King, Spades, Ace, Clubs, true)]    // піку — старшою пікою
        [InlineData(Spades, King, Spades, Queen, Clubs, false)] // молодшою пікою не можна
        [InlineData(Spades, Six, Clubs, Ace, Clubs, false)]     // піку не можна бити козирем
        [InlineData(Spades, Ace, Spades, Six, Clubs, true)]     // шістка пік б'є туза пік
        [InlineData(Hearts, Nine, Spades, Ace, Clubs, false)]   // пікою іншу масть не б'ють
        public void BeatingRules(Suit topSuit, Rank topRank, Suit mySuit, Rank myRank, Suit trump, bool expected)
        {
            var (game, p) = Playing(trump, new[] { C(Clubs, Ten) }, new[] { C(mySuit, myRank), C(Diamonds, Ten) });
            game.State.Table.Cards.Add(C(topSuit, topRank));
            game.State.Table.RoundSize = 2;
            game.State.CurrentPlayerIndex = 1;

            Assert.Null(game.Putting(p[1].Id, C(mySuit, myRank)));

            Assert.Equal(expected, game.State.Penalty == null);
            Assert.Equal(expected, !p[1].Hand.Contains(C(mySuit, myRank)));
        }

        [Fact]
        public void LeadCanBeAnyCard()
        {
            var (game, p) = Playing(Clubs, new[] { C(Hearts, Six), C(Clubs, Ten) }, new[] { C(Diamonds, Ten) });
            Assert.Null(game.Putting(p[0].Id, C(Hearts, Six)));
            Assert.Equal(p[1].Id, CurrentId(game));
        }

        [Fact]
        public void CannotPutCardNotInHand()
        {
            var (game, p) = Playing(Clubs, new[] { C(Hearts, Six) }, new[] { C(Diamonds, Ten) });
            Assert.NotNull(game.Putting(p[0].Id, C(Hearts, Ace)));
            Assert.Empty(game.State.Table.Cards);
        }

        [Fact]
        public void CannotPutOutOfTurn()
        {
            var (game, p) = Playing(Clubs, new[] { C(Hearts, Six) }, new[] { C(Diamonds, Ten) });
            Assert.NotNull(game.Putting(p[1].Id, C(Diamonds, Ten)));
        }

        // ---------- 2 етап: взяти карту ----------

        [Fact]
        public void TakingMovesBottomCardToHandAndPassesTurn()
        {
            var (game, p) = Playing(Clubs,
                new[] { C(Hearts, Six), C(Hearts, Eight) },
                new[] { C(Hearts, Seven), C(Hearts, Nine) },
                new[] { C(Diamonds, Ten) });
            Assert.Null(game.Putting(p[0].Id, C(Hearts, Six)));
            Assert.Null(game.Putting(p[1].Id, C(Hearts, Seven)));

            Assert.Null(game.Taking(p[2].Id));

            Assert.Contains(C(Hearts, Six), p[2].Hand);
            Assert.Equal(new[] { C(Hearts, Seven) }, game.State.Table.Cards);
            Assert.Equal(p[0].Id, CurrentId(game));
        }

        [Fact]
        public void CannotTakeFromEmptyTable()
        {
            var (game, p) = Playing(Clubs, new[] { C(Hearts, Six) }, new[] { C(Diamonds, Ten) });
            Assert.NotNull(game.Taking(p[0].Id));
        }

        [Fact]
        public void AfterTakingTheOnlyCardNextPlayerLeads()
        {
            var (game, p) = Playing(Clubs,
                new[] { C(Hearts, Six), C(Hearts, Eight) },
                new[] { C(Diamonds, Seven) },
                new[] { C(Diamonds, Ten) });
            Assert.Null(game.Putting(p[0].Id, C(Hearts, Six)));

            Assert.Null(game.Taking(p[1].Id));

            Assert.Empty(game.State.Table.Cards);
            Assert.Equal(p[2].Id, CurrentId(game));
        }

        // ---------- 2 етап: закриття кола ----------

        [Fact]
        public void RoundClosesWhenEveryoneHasPlayedAndCloserLeads()
        {
            var (game, p) = Playing(Clubs,
                new[] { C(Hearts, Six), C(Hearts, Ten) },
                new[] { C(Hearts, Seven), C(Hearts, Jack) });

            Assert.Null(game.Putting(p[0].Id, C(Hearts, Six)));
            Assert.Null(game.Putting(p[1].Id, C(Hearts, Seven)));

            Assert.Empty(game.State.Table.Cards);
            Assert.Equal(2, game.State.DiscardCount);
            Assert.Equal(p[1].Id, CurrentId(game));
            Assert.Equal(2, game.State.LastAction!.ClosedRound!.Count);
        }

        [Fact]
        public void TakenCardStillCountsTowardsClosingTheRound()
        {
            var (game, p) = Playing(Clubs,
                new[] { C(Hearts, Six), C(Hearts, Ace), C(Spades, Six) },
                new[] { C(Hearts, Seven), C(Spades, Seven) },
                new[] { C(Diamonds, Ten), C(Spades, Eight) });

            Assert.Null(game.Putting(p[0].Id, C(Hearts, Six)));
            Assert.Null(game.Putting(p[1].Id, C(Hearts, Seven)));
            Assert.Null(game.Taking(p[2].Id)); // на столі лишилась одна карта, але покладено вже дві
            Assert.Null(game.Putting(p[0].Id, C(Hearts, Ace)));

            Assert.Empty(game.State.Table.Cards);
            Assert.Equal(2, game.State.DiscardCount); // сімка і туз; узята шістка лишилась у третього гравця
            Assert.Contains(C(Hearts, Six), p[2].Hand);
            Assert.Equal(p[0].Id, CurrentId(game));
        }

        [Fact]
        public void TakingTheOnlyCardStartsNewRound()
        {
            var (game, p) = Playing(Clubs,
                new[] { C(Hearts, Six), C(Spades, Six) },
                new[] { C(Diamonds, Seven), C(Spades, Seven) },
                new[] { C(Diamonds, Ten), C(Spades, Eight) });

            Assert.Null(game.Putting(p[0].Id, C(Hearts, Six)));
            Assert.Null(game.Taking(p[1].Id));
            Assert.Null(game.Putting(p[2].Id, C(Diamonds, Ten)));

            Assert.Equal(1, game.State.Table.RoundPuts);
            Assert.Equal(3, game.State.Table.RoundSize);
        }

        [Fact]
        public void PlayerWhoWentOutStillCountsInTheRound()
        {
            var (game, p) = Playing(Clubs,
                new[] { C(Hearts, Six) },
                new[] { C(Hearts, Seven), C(Spades, Six) },
                new[] { C(Hearts, Eight), C(Spades, Seven) });

            Assert.Null(game.Putting(p[0].Id, C(Hearts, Six))); // перший викинув останню карту і вийшов
            Assert.Equal(1, p[0].Place);

            Assert.Null(game.Putting(p[1].Id, C(Hearts, Seven)));
            Assert.Equal(2, game.State.Table.Cards.Count); // коло ще не закрите: третій теж має покласти карту

            Assert.Null(game.Putting(p[2].Id, C(Hearts, Eight)));
            Assert.Empty(game.State.Table.Cards);
        }

        // ---------- По поганій ----------

        // Перший кладе чирву, а другий пробує побити її бубною: порушення
        private static (GameService Game, List<Player> Players) AfterFoul(Suit trump, Card[] second, params Card[][] others)
        {
            var hands = new List<Card[]> { new[] { C(Hearts, Nine), C(Hearts, Ten), C(Spades, Ace) }, second.Append(C(Diamonds, Ace)).ToArray() };
            hands.AddRange(others);
            var (game, p) = Playing(trump, hands.ToArray());
            Assert.Null(game.Putting(p[0].Id, C(Hearts, Nine)));
            Assert.Null(game.Putting(p[1].Id, C(Diamonds, Ace)));
            Assert.Equal(p[1].Id, game.State.Penalty!.ViolatorId);
            return (game, p);
        }

        [Fact]
        public void IllegalCardStaysInHandAndRoundDoesNotCountIt()
        {
            var (game, p) = AfterFoul(Clubs, new[] { C(Hearts, King) }, new[] { C(Spades, Seven) });

            Assert.Contains(C(Diamonds, Ace), p[1].Hand);
            Assert.Equal(new[] { C(Hearts, Nine) }, game.State.Table.Cards);
            Assert.Equal(1, game.State.Table.RoundPuts);
            Assert.Equal(p[1].Id, CurrentId(game));
            Assert.Equal(GameActionKind.Foul, game.State.LastAction!.Kind);
            Assert.Equal(C(Diamonds, Ace), game.State.LastAction.Card);
        }

        [Fact]
        public void EveryOtherPlayerGivesOneCardThenViolatorMovesAgain()
        {
            var (game, p) = AfterFoul(Clubs, new[] { C(Hearts, King) }, new[] { C(Spades, Seven), C(Spades, Eight) });
            int violatorBefore = p[1].Hand.Count;

            Assert.Equal(new[] { p[0].Id, p[2].Id }, game.State.Penalty!.PendingGiverIds);

            Assert.Null(game.GivingPenalty(p[0].Id, C(Hearts, Ten)));
            Assert.NotNull(game.State.Penalty);
            Assert.Null(game.GivingPenalty(p[2].Id, C(Spades, Seven)));
            Assert.Null(game.State.Penalty);

            Assert.Equal(violatorBefore + 2, p[1].Hand.Count);
            Assert.Contains(C(Hearts, Ten), p[1].Hand);
            Assert.Contains(C(Spades, Seven), p[1].Hand);
            Assert.DoesNotContain(C(Spades, Seven), p[2].Hand);

            Assert.Null(game.Putting(p[1].Id, C(Hearts, King)));
            Assert.Equal(2, game.State.Table.RoundPuts);
        }

        [Fact]
        public void NoMovesUntilEveryoneGave()
        {
            var (game, p) = AfterFoul(Clubs, new[] { C(Hearts, King) }, new[] { C(Spades, Seven) });

            Assert.NotNull(game.Putting(p[1].Id, C(Hearts, King)));
            Assert.NotNull(game.Taking(p[1].Id));
            Assert.Null(game.State.LastAction!.ClosedRound);
            Assert.Equal(new[] { C(Hearts, Nine) }, game.State.Table.Cards);
        }

        [Fact]
        public void OnlyOwedPlayersCanGiveAndOnlyOnce()
        {
            var (game, p) = AfterFoul(Clubs, new[] { C(Hearts, King) }, new[] { C(Spades, Seven), C(Spades, Eight) });

            Assert.NotNull(game.GivingPenalty(p[1].Id, C(Hearts, King))); // порушник сам собі не дає
            Assert.Null(game.GivingPenalty(p[2].Id, C(Spades, Seven)));
            Assert.NotNull(game.GivingPenalty(p[2].Id, C(Spades, Eight))); // другий раз не можна
            Assert.Contains(C(Spades, Eight), p[2].Hand);
        }

        [Theory]
        [InlineData(Clubs, new[] { "H7", "S7", "D9", "C6" }, new[] { "H7", "S7" })] // кілька найменших, козирна шістка не рахується
        [InlineData(Clubs, new[] { "CA", "D9", "H8" }, new[] { "H8" })]             // одна найменша некозирна
        [InlineData(Clubs, new[] { "C9", "C6", "CA" }, new[] { "C6" })]             // самі козирі: найменший із них
        public void LowestNonTrumpsAreSuggested(Suit trump, string[] hand, string[] expected)
        {
            static Card Parse(string text) => new(
                text[0] switch { 'C' => Clubs, 'D' => Diamonds, 'H' => Hearts, _ => Spades },
                text[1] switch { '6' => Six, '7' => Seven, '8' => Eight, '9' => Nine, _ => Ace });

            var (game, p) = Playing(trump,
                new[] { C(Hearts, Nine), C(Hearts, Ten) },
                new[] { C(Diamonds, Seven), C(Hearts, King) },
                hand.Select(Parse).ToArray());
            Assert.Null(game.Putting(p[0].Id, C(Hearts, Nine))); // некозирна бубна чирву не б'є: порушення
            Assert.Null(game.Putting(p[1].Id, C(Diamonds, Seven)));

            var view = game.GetViews(new RoomInfo("1234", "Тест", false)).Single(v => v.ConnectionId == p[2].ConnectionId).View;
            var suggested = view.Hand.Where(c => c.Suggested).Select(c => new Card(c.Suit, (Rank)c.Rank));

            Assert.Equal(expected.Select(Parse).OrderBy(c => c.ToString()), suggested.OrderBy(c => c.ToString()));
        }

        [Fact]
        public void AnyCardCanBeGivenNotOnlyTheSuggestedOne()
        {
            var (game, p) = AfterFoul(Clubs, new[] { C(Hearts, King) }, new[] { C(Clubs, Ace), C(Spades, Seven) });

            Assert.Null(game.GivingPenalty(p[2].Id, C(Clubs, Ace))); // козирний туз, хоча радили сімку

            Assert.Contains(C(Clubs, Ace), p[1].Hand);
            Assert.DoesNotContain(C(Clubs, Ace), p[2].Hand);
        }

        [Fact]
        public void CannotGiveCardNotInHand()
        {
            var (game, p) = AfterFoul(Clubs, new[] { C(Hearts, King) }, new[] { C(Spades, Seven) });
            Assert.NotNull(game.GivingPenalty(p[2].Id, C(Hearts, Six)));
            Assert.Single(p[2].Hand);
        }

        [Fact]
        public void GivingLastCardMidRoundWaitsForRoundEnd()
        {
            var (game, p) = AfterFoul(Clubs, new[] { C(Hearts, King) }, new[] { C(Spades, Seven) });
            p[2].Penky = new List<Card> { C(Clubs, Ace), C(Clubs, King) };

            Assert.Null(game.GivingPenalty(p[2].Id, C(Spades, Seven)));
            Assert.Null(game.GivingPenalty(p[0].Id, C(Hearts, Ten)));

            Assert.Empty(p[2].Hand);
            Assert.Equal(2, p[2].Penky.Count);
            Assert.True(p[2].IsActive);

            Assert.Null(game.Putting(p[1].Id, C(Hearts, King)));
            Assert.Equal(p[0].Id, CurrentId(game)); // третій чекає, його хід пропущено

            Assert.Null(game.Taking(p[0].Id));
            Assert.Null(game.Taking(p[1].Id)); // забрали останню карту зі столу: коло скінчилось

            Assert.Equal(2, p[2].Hand.Count);
            Assert.Empty(p[2].Penky);
            Assert.Equal(p[2].Id, CurrentId(game));
        }

        [Fact]
        public void GivingLastCardAtStageTwoStartTakesPenkyAtOnceAndKeepsGiving()
        {
            var (game, p) = LastDealWithFouls(0, 2, 0);
            p[0].Hand = new List<Card> { C(Spades, Six) };
            p[0].Penky = new List<Card> { C(Clubs, Six), C(Clubs, Seven) };

            Assert.Null(game.GivingPenalty(p[0].Id, C(Spades, Six)));

            Assert.Equal(2, p[0].Hand.Count);
            Assert.Empty(p[0].Penky);
            Assert.Null(game.GivingPenalty(p[0].Id, C(Clubs, Six))); // другу карту віддає вже з пеньків
        }

        [Fact]
        public void PlayerWaitingForPenkyOwesNothing()
        {
            var (game, p) = Playing(Clubs,
                new[] { C(Hearts, Six) },
                new[] { C(Diamonds, Seven), C(Hearts, Seven) },
                new[] { C(Spades, Eight), C(Spades, Nine) });
            p[0].Penky = new List<Card> { C(Clubs, Six) };

            Assert.Null(game.Putting(p[0].Id, C(Hearts, Six)));
            Assert.Null(game.Putting(p[1].Id, C(Diamonds, Seven))); // бубна чирву не б'є

            Assert.Equal(new[] { p[2].Id }, game.State.Penalty!.PendingGiverIds);
        }

        [Fact]
        public void FoulWhenEveryoneElseWaitsCostsNothing()
        {
            var (game, p) = Playing(Clubs,
                new[] { C(Hearts, Six) },
                new[] { C(Diamonds, Seven), C(Hearts, Seven) });
            p[0].Penky = new List<Card> { C(Clubs, Six) };

            Assert.Null(game.Putting(p[0].Id, C(Hearts, Six)));
            Assert.Null(game.Putting(p[1].Id, C(Diamonds, Seven)));

            Assert.Equal(GameActionKind.Foul, game.State.LastAction!.Kind);
            Assert.Null(game.State.Penalty);
            Assert.Equal(p[1].Id, CurrentId(game));
            Assert.Null(game.Putting(p[1].Id, C(Hearts, Seven))); // гра не завмерла
        }

        [Fact]
        public void GivingLastCardWithoutPenkyFinishesPlayer()
        {
            var (game, p) = AfterFoul(Clubs, new[] { C(Hearts, King) }, new[] { C(Spades, Seven) });

            Assert.Null(game.GivingPenalty(p[2].Id, C(Spades, Seven)));

            Assert.Equal(1, p[2].Place);
            Assert.Equal(GamePhase.InProgress, game.State.Phase);
            Assert.NotNull(game.State.Penalty); // перший ще не віддав
        }

        [Fact]
        public void ViolatorLosesWhenEveryoneGaveAwayTheirLastCard()
        {
            var (game, p) = Playing(Clubs,
                new[] { C(Hearts, Nine), C(Hearts, Ten) },
                new[] { C(Diamonds, Ace) });
            Assert.Null(game.Putting(p[0].Id, C(Hearts, Nine)));
            Assert.Null(game.Putting(p[1].Id, C(Diamonds, Ace)));

            Assert.Null(game.GivingPenalty(p[0].Id, C(Hearts, Ten)));

            Assert.Equal(GamePhase.Finished, game.State.Phase);
            Assert.Equal(p[1].Id, game.State.LoserId);
            Assert.Null(game.State.Penalty);
        }

        [Fact]
        public void GiverLeavingStopsTheWaitForThem()
        {
            var (game, p) = AfterFoul(Clubs, new[] { C(Hearts, King) }, new[] { C(Spades, Seven) });
            Assert.Null(game.GivingPenalty(p[0].Id, C(Hearts, Ten)));

            Assert.True(game.Leave(p[2].Id));

            Assert.Null(game.State.Penalty);
            Assert.Null(game.Putting(p[1].Id, C(Hearts, King)));
        }

        [Fact]
        public void ViolatorLeavingCancelsPenalty()
        {
            var (game, p) = AfterFoul(Clubs, new[] { C(Hearts, King) }, new[] { C(Spades, Seven) });

            Assert.True(game.Leave(p[1].Id));

            Assert.Null(game.State.Penalty);
            Assert.Equal(GamePhase.InProgress, game.State.Phase);
            Assert.NotNull(game.GivingPenalty(p[0].Id, C(Hearts, Ten)));
        }

        [Fact]
        public void PenaltyCardIsHiddenFromThirdPlayers()
        {
            var (game, p) = AfterFoul(Clubs, new[] { C(Hearts, King) }, new[] { C(Spades, Seven), C(Spades, Eight) });

            Assert.Null(game.GivingPenalty(p[2].Id, C(Spades, Seven)));
            var views = game.GetViews(new RoomInfo("1234", "Тест", false));

            Assert.Null(views.Single(v => v.ConnectionId == p[0].ConnectionId).View.LastAction!.Card);
            Assert.NotNull(views.Single(v => v.ConnectionId == p[1].ConnectionId).View.LastAction!.Card);
            Assert.NotNull(views.Single(v => v.ConnectionId == p[2].ConnectionId).View.LastAction!.Card);
            Assert.DoesNotContain("♠", game.State.Log[^1]);
        }

        [Fact]
        public void RematchClearsPenalty()
        {
            var (game, p) = AfterFoul(Clubs, new[] { C(Hearts, King) }, new[] { C(Spades, Seven) });
            game.State.Phase = GamePhase.Finished;

            Assert.Null(game.StartGame(p[0].Id));

            Assert.Null(game.State.Penalty);
        }

        // ---------- Пеньки і кінець гри ----------

        [Fact]
        public void EmptyHandMidRoundWaitsAndTakesPenkyWhenRoundCloses()
        {
            var (game, p) = Playing(Clubs,
                new[] { C(Hearts, Six) },
                new[] { C(Hearts, Seven), C(Diamonds, Nine) },
                new[] { C(Hearts, Eight), C(Diamonds, Ten) });
            p[0].Penky = new List<Card> { C(Spades, Six), C(Spades, Seven) };

            Assert.Null(game.Putting(p[0].Id, C(Hearts, Six)));

            Assert.Empty(p[0].Hand);
            Assert.Equal(2, p[0].Penky.Count);
            Assert.True(p[0].IsActive);
            Assert.Equal(p[1].Id, CurrentId(game));

            Assert.Null(game.Putting(p[1].Id, C(Hearts, Seven)));
            Assert.Null(game.Putting(p[2].Id, C(Hearts, Eight))); // коло закрито

            Assert.Equal(2, p[0].Hand.Count);
            Assert.Empty(p[0].Penky);
            Assert.Equal(p[2].Id, CurrentId(game));
        }

        [Fact]
        public void WaitingPlayerIsSkippedAndCannotMove()
        {
            var (game, p) = Playing(Clubs,
                new[] { C(Hearts, Six) },
                new[] { C(Hearts, Seven), C(Diamonds, Nine) },
                new[] { C(Spades, Eight), C(Diamonds, Ten) });
            p[0].Penky = new List<Card> { C(Spades, Six) };

            Assert.Null(game.Putting(p[0].Id, C(Hearts, Six)));
            Assert.Null(game.Putting(p[1].Id, C(Hearts, Seven)));
            Assert.Null(game.Taking(p[2].Id));

            Assert.Equal(p[1].Id, CurrentId(game)); // перший без карт: хід знову в другого
            Assert.NotNull(game.Taking(p[0].Id));
            Assert.Empty(p[0].Hand);
        }

        [Fact]
        public void TakingLastTableCardEndsRoundAndGivesPenky()
        {
            var (game, p) = Playing(Clubs, new[] { C(Hearts, Six) }, new[] { C(Diamonds, Ten) });
            p[0].Penky = new List<Card> { C(Spades, Six), C(Spades, Seven) };

            Assert.Null(game.Putting(p[0].Id, C(Hearts, Six)));
            Assert.Null(game.Taking(p[1].Id));

            Assert.Equal(2, p[0].Hand.Count);
            Assert.Empty(p[0].Penky);
            Assert.Equal(p[0].Id, CurrentId(game));
        }

        [Fact]
        public void ClosingRoundWithLastCardTakesPenkyAndLeads()
        {
            var (game, p) = Playing(Clubs, new[] { C(Hearts, Six), C(Spades, Eight) }, new[] { C(Hearts, Seven) });
            p[1].Penky = new List<Card> { C(Diamonds, Six), C(Diamonds, Seven) };

            Assert.Null(game.Putting(p[0].Id, C(Hearts, Six)));
            Assert.Null(game.Putting(p[1].Id, C(Hearts, Seven)));

            Assert.Equal(2, p[1].Hand.Count);
            Assert.Equal(p[1].Id, CurrentId(game));
        }

        [Fact]
        public void PlayerWaitingForPenkyLosesIfEveryoneElseWentOut()
        {
            var (game, p) = Playing(Clubs,
                new[] { C(Hearts, Six) },
                new[] { C(Hearts, Seven) });
            p[0].Penky = new List<Card> { C(Spades, Six) };

            Assert.Null(game.Putting(p[0].Id, C(Hearts, Six)));
            Assert.Null(game.Putting(p[1].Id, C(Hearts, Seven))); // другий вийшов, а перший ще з пеньками

            Assert.Equal(GamePhase.Finished, game.State.Phase);
            Assert.Equal(p[0].Id, game.State.LoserId);
        }

        [Fact]
        public void LastPlayerWithCardsLoses()
        {
            var (game, p) = Playing(Clubs, new[] { C(Hearts, Six) }, new[] { C(Diamonds, Ten) });

            Assert.Null(game.Putting(p[0].Id, C(Hearts, Six)));

            Assert.Equal(GamePhase.Finished, game.State.Phase);
            Assert.Equal(1, p[0].Place);
            Assert.Equal(2, p[1].Place);
            Assert.Equal(p[1].Id, game.State.LoserId);
        }

        [Fact]
        public void TurnSkipsPlayersWhoWentOut()
        {
            var (game, p) = Playing(Clubs,
                new[] { C(Hearts, Six), C(Diamonds, Seven) },
                new[] { C(Hearts, Seven) },
                new[] { C(Hearts, Eight), C(Diamonds, Eight) });
            game.State.CurrentPlayerIndex = 1;

            Assert.Null(game.Putting(p[1].Id, C(Hearts, Seven))); // другий вийшов
            Assert.Null(game.Taking(p[2].Id));                    // третій забрав карту, стіл порожній
            Assert.Null(game.Putting(p[0].Id, C(Diamonds, Seven)));

            Assert.Equal(p[2].Id, CurrentId(game)); // другого пропущено
        }

        [Fact]
        public void RematchResetsEverything()
        {
            var (game, p) = Playing(Clubs, new[] { C(Hearts, Six) }, new[] { C(Diamonds, Ten) });
            Assert.Null(game.Putting(p[0].Id, C(Hearts, Six)));
            Assert.Equal(GamePhase.Finished, game.State.Phase);

            Assert.Null(game.StartGame(p[0].Id));

            Assert.Equal(GamePhase.InProgress, game.State.Phase);
            Assert.All(p, x => Assert.Null(x.Place));
            Assert.Null(game.State.LoserId);
            Assert.Null(game.State.Trump);
            Assert.Equal(0, game.State.DiscardCount);
        }

        // ---------- Вихід гравців ----------

        [Fact]
        public void LeavingOnOwnTurnPassesTurnAndReturnsDrawnCard()
        {
            var (game, p) = Dealing(new[] { C(Clubs, Seven), C(Hearts, Nine), C(Hearts, Ace) }, C(Spades, Jack), C(Clubs, King));
            Assert.Null(game.Drawing(p[0].Id));

            Assert.True(game.Leave(p[0].Id));

            Assert.Null(game.State.DrawnCard);
            Assert.Equal(C(Clubs, King), game.State.Deck[^1]);
            Assert.Equal(p[1].Id, CurrentId(game));
        }

        [Fact]
        public void LeavingBeforeCurrentKeepsTurnOnSamePlayer()
        {
            var (game, p) = Playing(Clubs, new[] { C(Hearts, Six) }, new[] { C(Hearts, Seven) }, new[] { C(Hearts, Eight) });
            game.State.CurrentPlayerIndex = 2;

            Assert.True(game.Leave(p[0].Id));

            Assert.Equal(p[2].Id, CurrentId(game));
        }

        [Fact]
        public void LeavingLastOpponentAbandonsGameWithoutLoser()
        {
            var (game, p) = Playing(Clubs, new[] { C(Hearts, Six) }, new[] { C(Hearts, Seven) });

            Assert.True(game.Leave(p[1].Id));

            Assert.Equal(GamePhase.Finished, game.State.Phase);
            Assert.Null(game.State.LoserId);
        }

        [Fact]
        public void LeavingMidRoundShrinksTheRound()
        {
            var (game, p) = Playing(Clubs,
                new[] { C(Hearts, Six), C(Spades, Ten) },
                new[] { C(Hearts, Seven), C(Spades, Jack) },
                new[] { C(Hearts, Eight) });
            Assert.Null(game.Putting(p[0].Id, C(Hearts, Six)));
            Assert.Null(game.Putting(p[1].Id, C(Hearts, Seven)));

            Assert.True(game.Leave(p[2].Id));

            Assert.Empty(game.State.Table.Cards);
            Assert.Equal(GamePhase.InProgress, game.State.Phase);
        }

        [Fact]
        public void HostPassesToNextPlayerOnLeave()
        {
            var (game, p) = NewTable(3);
            Assert.True(game.Leave(p[0].Id));
            Assert.Equal(p[1].Id, game.State.HostId);
        }

        [Fact]
        public void ExpiredOfflinePlayerIsRemovedButReturningOneStays()
        {
            var (game, p) = NewTable(3);
            var t = DateTime.UtcNow;
            game.MarkDisconnected(p[1].Id, t);
            game.MarkDisconnected(p[2].Id, t);
            game.Reconnect(p[2].Token, "new-conn", out _);

            Assert.False(game.RemoveExpired(t + GameService.GraceOutOfGame - TimeSpan.FromSeconds(1)));
            Assert.True(game.RemoveExpired(t + GameService.GraceOutOfGame));

            Assert.DoesNotContain(p[1], game.State.Players);
            Assert.Contains(p[2], game.State.Players);
        }

        // ---------- Що бачить клієнт ----------

        [Fact]
        public void ViewDoesNotLeakHiddenCards()
        {
            var (game, p) = Playing(Clubs, new[] { C(Hearts, Six), C(Hearts, Nine) }, new[] { C(Diamonds, Ten) });
            p[0].Penky = new List<Card> { C(Spades, Ace) };
            var info = new RoomInfo("1234", "Тест", false);

            var view = game.GetViews(info).Single(v => v.ConnectionId == p[1].ConnectionId).View;
            var json = System.Text.Json.JsonSerializer.Serialize(view);

            Assert.Equal(new[] { C(Diamonds, Ten) }, view.Hand.Select(c => new Card(c.Suit, (Rank)c.Rank)));
            Assert.DoesNotContain("Token", json);
            Assert.DoesNotContain(p[0].Token, json);
            Assert.Equal(2, view.Players[0].HandCount);
            Assert.Null(view.Players[0].TopCard);
        }

        [Fact]
        public void PilesShowOnlyTopCardDuringDealing()
        {
            var (game, p) = Dealing(new[] { C(Clubs, Seven), C(Hearts, Nine) }, C(Spades, Jack));
            p[1].Hand.Add(C(Diamonds, King));
            var info = new RoomInfo("1234", "Тест", false);

            var view = game.GetViews(info).Single(v => v.ConnectionId == p[0].ConnectionId).View;

            Assert.Equal(Diamonds, view.Players[1].TopCard!.Suit);
            Assert.Equal(13, view.Players[1].TopCard!.Rank);
            Assert.Equal(2, view.Players[1].HandCount);
            Assert.Empty(view.Hand);
        }

        [Fact]
        public void ViewDoesNotRevealLegalMoves()
        {
            var (game, p) = Playing(Clubs,
                new[] { C(Hearts, Six), C(Diamonds, Six) },
                new[] { C(Hearts, Seven), C(Diamonds, Ten), C(Clubs, Six) },
                new[] { C(Hearts, Eight) });
            Assert.Null(game.Putting(p[0].Id, C(Hearts, Six)));
            var views = game.GetViews(new RoomInfo("1234", "Тест", false));

            var mover = views.Single(v => v.ConnectionId == p[1].ConnectionId).View;
            var waiting = views.Single(v => v.ConnectionId == p[2].ConnectionId).View;

            Assert.All(mover.Hand, c => Assert.False(c.Suggested));
            Assert.True(mover.CanTake);
            Assert.False(waiting.CanTake);
            Assert.Null(mover.Penalty);

            var json = System.Text.Json.JsonSerializer.Serialize(mover);
            Assert.DoesNotContain("CanPut", json);
            Assert.DoesNotContain("DealTargets", json);
        }

        [Fact]
        public void ViewDuringPenaltyBlocksMovesAndMarksOnlyGiver()
        {
            var (game, p) = AfterFoul(Clubs, new[] { C(Hearts, King) }, new[] { C(Spades, Seven), C(Spades, Eight) });
            var views = game.GetViews(new RoomInfo("1234", "Тест", false));

            var violator = views.Single(v => v.ConnectionId == p[1].ConnectionId).View;
            var giver = views.Single(v => v.ConnectionId == p[2].ConnectionId).View;

            Assert.False(violator.CanTake);
            Assert.All(violator.Hand, c => Assert.False(c.Suggested));
            Assert.Equal(new[] { true, false }, giver.Hand.Select(c => c.Suggested));
            Assert.Equal(p[1].Id, giver.Penalty!.ViolatorId);
            Assert.Equal(new[] { p[0].Id, p[2].Id }, giver.Penalty.WaitingFor);
        }
    }
}
