using System.Security.Cryptography;
using DurakBackend.DTOs;
using DurakBackend.Models;

namespace DurakBackend.Services
{
    // Правила й стан одного столу. На кожну кімнату — свій екземпляр (див. RoomManager).
    // Методи ходів повертають null, якщо все гаразд, або текст помилки для гравця.
    public class GameService
    {
        public const int MinPlayers = 2;
        public const int MaxPlayers = 6;
        private const int LogLimit = 100;
        private const int LogLinesForClient = 40;

        // Скільки чекаємо на відключеного гравця, перш ніж прибрати його зі столу
        public static readonly TimeSpan GraceInGame = TimeSpan.FromSeconds(90);
        public static readonly TimeSpan GraceOutOfGame = TimeSpan.FromSeconds(30);

        private readonly object _lock = new();
        private readonly Random _rng;

        public int Capacity { get; }
        public GameState State { get; } = new();

        public GameService(int capacity = MaxPlayers, Random? rng = null)
        {
            Capacity = capacity;
            _rng = rng ?? Random.Shared;
        }

        private void Shuffle<T>(List<T> list)
        {
            int n = list.Count;
            while (n > 1)
            {
                n--;
                int k = _rng.Next(n + 1);
                (list[k], list[n]) = (list[n], list[k]);
            }
        }

        private void AddLog(string message)
        {
            State.Log.Add(message);
            if (State.Log.Count > LogLimit) State.Log.RemoveAt(0);
        }

        private List<Player> ActivePlayers => State.Players.Where(p => p.IsActive).ToList();

        private Player? FindPlayer(string playerId) => State.Players.FirstOrDefault(p => p.Id == playerId);

        // ---------- Гравці ----------

        public Player? Join(string nick, string connectionId, out string? error)
        {
            lock (_lock)
            {
                error = null;
                if (State.Phase == GamePhase.InProgress) error = "Гра вже почалася, зайти не можна";
                else if (State.Players.Count >= Capacity) error = "Кімната заповнена";
                else if (State.Players.Any(p => string.Equals(p.Nick, nick, StringComparison.OrdinalIgnoreCase)))
                    error = "Такий нікнейм уже є в цій кімнаті";
                if (error != null) return null;

                var player = new Player
                {
                    Id = NewPlayerId(),
                    Token = NewToken(),
                    ConnectionId = connectionId,
                    Nick = nick
                };
                State.Players.Add(player);
                State.HostId ??= player.Id;
                AddLog($"{nick} заходить у кімнату");
                State.Version++;
                return player;
            }
        }

        private static string NewPlayerId() => Guid.NewGuid().ToString("N")[..8];
        private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        public string? AddBot(string hostId, BotLevel level)
        {
            lock (_lock)
            {
                if (State.HostId != hostId) return "Додавати ботів може лише господар кімнати";
                if (State.Phase == GamePhase.InProgress) return "Гра вже почалася";
                if (State.Players.Count >= Capacity) return "Кімната заповнена";

                // Імен більше, ніж місць за столом, тож вільне ім'я знайдеться завжди
                var freeNames = BotNames
                    .Where(name => !State.Players.Any(p => string.Equals(p.Nick, name, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                var nick = freeNames[_rng.Next(freeNames.Count)];

                // Токен такий самий секретний, як у людей: зайняти місце бота через Rejoin не вийде
                State.Players.Add(new Player { Id = NewPlayerId(), Token = NewToken(), Nick = nick, BotLevel = level });
                AddLog($"{nick} сідає за стіл");
                State.Version++;
                return null;
            }
        }

        public bool Leave(string playerId)
        {
            lock (_lock)
            {
                var index = State.Players.FindIndex(p => p.Id == playerId);
                if (index < 0) return false;

                var player = State.Players[index];
                bool inProgress = State.Phase == GamePhase.InProgress;

                // Витягнута, але не покладена карта повертається на колоду — її витягне наступний
                if (inProgress && index == State.CurrentPlayerIndex && State.DrawnCard != null)
                {
                    State.Deck.Add(State.DrawnCard);
                    State.DrawnCard = null;
                }

                State.Players.RemoveAt(index);
                AddLog($"{player.Nick} виходить із кімнати");

                if (State.HostId == player.Id)
                {
                    // Боти господарями не бувають
                    var newHost = State.Players.FirstOrDefault(p => p.ConnectionId != null) ?? State.Players.FirstOrDefault(p => !p.IsBot);
                    State.HostId = newHost?.Id;
                    if (newHost != null) AddLog($"{newHost.Nick} тепер господар кімнати");
                }

                if (inProgress)
                {
                    if (index < State.CurrentPlayerIndex) State.CurrentPlayerIndex--;
                    else if (State.CurrentPlayerIndex >= State.Players.Count) State.CurrentPlayerIndex = 0;

                    if (ActivePlayers.Count < MinPlayers) AbandonGame();
                    else
                    {
                        ShrinkRound();
                        if (!CanMove(State.Players[State.CurrentPlayerIndex])) NextTurn();
                        DropFromPenalty(player);
                    }
                }

                State.Version++;
                return true;
            }
        }

        // Гравець вийшов посеред кола: чекати на його карту вже не треба
        private void ShrinkRound()
        {
            if (State.Table.Cards.Count == 0) return;
            State.Table.RoundSize = Math.Min(State.Table.RoundSize, ActivePlayers.Count);
            if (RoundIsOver) CloseRound();
        }

        public Player? Reconnect(string token, string connectionId, out string? previousConnectionId)
        {
            lock (_lock)
            {
                previousConnectionId = null;
                var player = State.Players.FirstOrDefault(p => p.Token == token);
                if (player == null) return null;

                previousConnectionId = player.ConnectionId;
                player.ConnectionId = connectionId;
                player.DisconnectedAt = null;
                State.Version++;
                return player;
            }
        }

        // Чи чекає на цього гравця вільне місце (якщо він онлайн на іншій вкладці, то не чекає)
        public bool HasOfflineSeat(string token)
        {
            lock (_lock)
            {
                return State.Players.Any(p => p.Token == token && p.ConnectionId == null);
            }
        }

        public void MarkDisconnected(string playerId, DateTime now)
        {
            lock (_lock)
            {
                var player = FindPlayer(playerId);
                if (player == null) return;
                player.ConnectionId = null;
                player.DisconnectedAt = now;
                State.Version++;
            }
        }

        // Прибирає тих, хто так і не повернувся. Повертає true, якщо хтось вибув.
        public bool RemoveExpired(DateTime now)
        {
            lock (_lock)
            {
                var grace = State.Phase == GamePhase.InProgress ? GraceInGame : GraceOutOfGame;
                var expired = State.Players
                    .Where(p => p.DisconnectedAt is { } at && now - at >= grace)
                    .ToList();
                foreach (var player in expired) Leave(player.Id);
                return expired.Count > 0;
            }
        }

        public string? CheckKick(string hostId, string targetId)
        {
            lock (_lock)
            {
                if (State.HostId != hostId) return "Вигнати гравця може лише господар кімнати";
                if (hostId == targetId) return "Себе вигнати не можна";
                if (FindPlayer(targetId) == null) return "Такого гравця немає";
                return null;
            }
        }

        public string? GetConnectionId(string playerId)
        {
            lock (_lock)
            {
                return FindPlayer(playerId)?.ConnectionId;
            }
        }

        // ---------- Початок і кінець гри ----------

        public string? StartGame(string playerId)
        {
            lock (_lock)
            {
                if (State.HostId != playerId) return "Почати гру може лише господар кімнати";
                if (State.Phase == GamePhase.InProgress) return "Гра вже триває";
                if (State.Players.Count < MinPlayers) return $"Потрібно щонайменше {MinPlayers} гравці";

                // Перезбираємо колоду і забираємо карти
                State.Deck = GameRules.FullDeck.ToList();
                Shuffle(State.Deck);
                foreach (var p in State.Players)
                {
                    p.Hand.Clear();
                    p.Penky.Clear();
                    p.Place = null;
                    p.Fouls = 0;
                }

                // Роздаємо кожному гравцеві по 2 пеньки і одну відкриту карту
                foreach (var p in State.Players)
                {
                    for (int i = 0; i < 2; i++) p.Penky.Add(PopDeck());
                    p.Hand.Add(PopDeck());
                }

                // Боти починають партію з чистою пам'яттю: відомі лише щойно роздані відкриті карти
                foreach (var bot in State.Players.Where(p => p.IsBot))
                {
                    bot.Memory = new BotMemory();
                    foreach (var p in State.Players) bot.Memory.SawOpenCard(p.Id, p.Hand[0]);
                }

                State.LastNonSpade = State.Players.Select(p => p.Hand[0]).LastOrDefault(c => c.Suit != Suit.Spades);

                State.CurrentPlayerIndex = _rng.Next(State.Players.Count);
                State.Table = new TableState();
                State.DiscardCount = 0;
                State.FinishedCount = 0;
                State.DrawnCard = null;
                State.Penalty = null;
                State.Trump = null;
                State.LoserId = null;
                State.LastAction = null;
                State.Phase = GamePhase.InProgress;
                State.SubPhase = InProgressPhase.Dealing;
                State.Log.Clear();
                AddLog($"Гру почато. Першим тягне {State.Players[State.CurrentPlayerIndex].Nick}");
                State.Version++;
                return null;
            }
        }

        private Card PopDeck()
        {
            var card = State.Deck[^1];
            State.Deck.RemoveAt(State.Deck.Count - 1);
            return card;
        }

        // Залишився один гравець з картами — він і програв
        private bool TryFinishGame()
        {
            var active = ActivePlayers;
            if (active.Count > 1) return false;

            if (active.Count == 1)
            {
                var loser = active[0];
                loser.Place = ++State.FinishedCount;
                State.LoserId = loser.Id;
                AddLog($"Гру закінчено: {loser.Nick} залишається пеньком!");
            }
            EndGame();
            return true;
        }

        // Суперники вийшли, грати далі нема з ким: переможених тут немає
        private void AbandonGame()
        {
            AddLog("Гру перервано: не вистачає гравців");
            EndGame();
        }

        private void EndGame()
        {
            State.Phase = GamePhase.Finished;
            State.SubPhase = null;
            State.DrawnCard = null;
            State.Penalty = null;
            State.DiscardCount += State.Table.Cards.Count;
            State.Table.Cards.Clear();
        }

        // На 2 етапі ходити може лише той, у кого є карти в руці: хто чекає на свої пеньки, пропускає хід до кінця кола.
        // На 1 етапі порожня купка не заважає: гравець усе одно тягне карту.
        private bool CanMove(Player player) =>
            player.IsActive && (State.SubPhase == InProgressPhase.Dealing || player.Hand.Count > 0);

        // Хід переходить наступному гравцеві, який може ходити
        private void NextTurn()
        {
            var count = State.Players.Count;
            for (int i = 1; i <= count; i++)
            {
                var next = (State.CurrentPlayerIndex + i) % count;
                if (CanMove(State.Players[next]))
                {
                    State.CurrentPlayerIndex = next;
                    return;
                }
            }
        }

        private string? CheckTurn(string playerId, InProgressPhase phase, out Player player)
        {
            player = FindPlayer(playerId)!;
            if (player == null) return "Вас немає за цим столом";
            if (State.Phase != GamePhase.InProgress || State.SubPhase != phase) return "Зараз так ходити не можна";
            if (State.Players[State.CurrentPlayerIndex] != player) return "Зараз не ваш хід";
            if (State.Penalty != null) return "Зачекайте: гравці віддають карти по поганій";
            return null;
        }

        // ---------- Видача по поганій ----------

        // Хід не за правилами не відбувається: карта лишається в порушника, хід теж
        private void NoteFoul(Player violator, Card card, string reason) =>
            Publish(GameActionKind.Foul, violator.Id, card: card, reason: reason);

        // Кожен інший гравець, у кого є карти в руці, винен порушникові по карті за кожен провтик.
        // Хто чекає на пеньки, тому віддавати нічого: пеньки він ще не взяв.
        private void StartPenalty(Player violator, int fouls)
        {
            var givers = ActivePlayers
                .Where(p => p != violator && p.Hand.Count > 0)
                .SelectMany(p => Enumerable.Repeat(p.Id, fouls))
                .ToList();
            if (givers.Count > 0) State.Penalty = new PenaltyState(violator.Id, givers);
        }

        // Провтики 1 етапу віддаємо на початку 2 етапу: по черзі, починаючи від того, хто ходить
        private void StartNextFoulPenalty()
        {
            if (ActivePlayers.Count < MinPlayers) return;

            for (int i = 0; i < State.Players.Count; i++)
            {
                var violator = State.Players[(State.CurrentPlayerIndex + i) % State.Players.Count];
                if (violator.Fouls == 0 || !violator.IsActive) continue;

                AddLog($"{violator.Nick} має провтиків на 1 етапі: {violator.Fouls}. Кожен віддає стільки ж карт по поганій");
                StartPenalty(violator, violator.Fouls);
                violator.Fouls = 0;
                return;
            }
        }

        // На того, хто вийшов, більше не чекаємо; порушник забирає свою видачу із собою
        private void DropFromPenalty(Player leaver)
        {
            if (State.Penalty is not { } penalty) return;

            penalty.PendingGiverIds.RemoveAll(id => id == leaver.Id);
            if (penalty.ViolatorId == leaver.Id || penalty.PendingGiverIds.Count == 0)
            {
                State.Penalty = null;
                StartNextFoulPenalty();
            }
        }

        // Порада, яку карту віддати: найменші некозирні. Самі козирі — найменший із них.
        private List<Card> GetSuggestedGives(Player giver)
        {
            var notTrumps = giver.Hand.Where(c => c.Suit != State.Trump?.Suit).ToList();
            var pool = notTrumps.Count > 0 ? notTrumps : giver.Hand;
            var lowest = pool.Min(c => c.Rank);
            return pool.Where(c => c.Rank == lowest).ToList();
        }

        public string? GivingPenalty(string playerId, Card card)
        {
            lock (_lock)
            {
                if (State.Penalty is not { } penalty || !penalty.PendingGiverIds.Contains(playerId))
                    return "Зараз вам не треба віддавати карту";

                var giver = FindPlayer(playerId)!;
                var violator = FindPlayer(penalty.ViolatorId)!;
                if (!giver.Hand.Contains(card)) return "У вас немає такої карти";

                giver.Hand.Remove(card);
                violator.Hand.Add(card);
                penalty.PendingGiverIds.Remove(playerId);
                AddLog($"{giver.Nick} віддає карту гравцю {violator.Nick}");

                // На старті 2 етапу кола ще немає, тож пеньки беруться одразу і віддавати далі можна вже з них.
                // Посеред кола гравець чекає його кінця, і більше віддавати йому нічим.
                if (State.Table.Cards.Count == 0) TakeWaitingPenky();
                FinishIfNoCards(giver);
                if (giver.Hand.Count == 0)
                {
                    penalty.PendingGiverIds.RemoveAll(id => id == playerId);
                    if (!CanMove(State.Players[State.CurrentPlayerIndex])) NextTurn();
                }

                if (penalty.PendingGiverIds.Count == 0)
                {
                    State.Penalty = null;
                    StartNextFoulPenalty();
                }

                Publish(GameActionKind.PenaltyGive, giver.Id, violator.Id, card);

                TryFinishGame();
                return null;
            }
        }

        // Для тестів і ботів. На клієнт це не йде: гравець має вирішувати сам.
        public List<string> GetLegalDealTargets(string playerId)
        {
            lock (_lock)
            {
                var player = FindPlayer(playerId);
                return player != null && State.DrawnCard != null
                    ? new List<string> { GetDealTarget(player, State.DrawnCard).Id }
                    : new List<string>();
            }
        }

        public string? GetRequiredMoveTarget(string playerId)
        {
            lock (_lock)
            {
                var player = FindPlayer(playerId);
                return player != null && player.Hand.Count > 0 ? FirstFitting(player, player.Hand[^1])?.Id : null;
            }
        }

        public List<Card> GetLegalPuts(string playerId)
        {
            lock (_lock)
            {
                return FindPlayer(playerId)?.Hand.Where(CanPut).ToList() ?? new List<Card>();
            }
        }

        // ---------- 1 етап: набір карт ----------

        // Перший зліва направо суперник, якому підходить карта. На екрані суперники сидять саме в такому порядку:
        // зліва той, хто ходить після гравця.
        private Player? FirstFitting(Player player, Card card)
        {
            int me = State.Players.IndexOf(player);
            for (int i = 1; i < State.Players.Count; i++)
            {
                var other = State.Players[(me + i) % State.Players.Count];
                if (other.Hand.Count > 0 && GameRules.FitsPile(card, other.Hand[^1])) return other;
            }
            return null;
        }

        // Куди має лягти витягнута карта: першому зліва направо, кому вона підходить. Якщо нікому — собі.
        private Player GetDealTarget(Player player, Card card) => FirstFitting(player, card) ?? player;

        // Тягнемо верхню карту з колоди: її бачать усі
        public string? Drawing(string playerId)
        {
            lock (_lock)
            {
                var error = CheckTurn(playerId, InProgressPhase.Dealing, out var player);
                if (error != null) return error;
                if (State.DrawnCard != null) return "Спершу покладіть витягнуту карту";

                // Верхню карту перекладати обов'язково: хто забув і потягнувся до колоди, той зробив провтик
                if (player.Hand.Count > 0 && FirstFitting(player, player.Hand[^1]) != null)
                {
                    var top = player.Hand[^1];
                    player.Fouls++;
                    AddLog($"{player.Nick} не перекладає {top}: провтик");
                    NoteFoul(player, top, $"{top} треба перекласти");
                    return null;
                }

                State.DrawnCard = PopDeck();
                Publish(GameActionKind.Draw, player.Id, card: State.DrawnCard);
                return null;
            }
        }

        // Кладемо витягнуту карту гравцеві на гору під час 1 етапу гри
        public string? Dealing(string playerId, string targetId)
        {
            lock (_lock)
            {
                var error = CheckTurn(playerId, InProgressPhase.Dealing, out var player);
                if (error != null) return error;
                if (State.DrawnCard is not { } card) return "Спершу витягніть карту з колоди";

                var target = FindPlayer(targetId);
                if (target == null) return "Такого гравця немає";
                if (GetDealTarget(player, card) != target)
                {
                    // Провтик на 1 етапі лише рахуємо: карти за нього віддають на початку 2 етапу
                    player.Fouls++;
                    if (target == player)
                    {
                        AddLog($"{player.Nick} кладе {card} собі не за правилами: провтик");
                        NoteFoul(player, card, $"{card} підходить суперникові");
                    }
                    else
                    {
                        AddLog($"{player.Nick} кладе {card} на {target.Nick} не за правилами: провтик");
                        NoteFoul(player, card, NotFitReason(player, card, target));
                    }
                    return null;
                }

                bool toSelf = target == player;
                bool fitsOwnPile = toSelf && player.Hand.Count > 0 && GameRules.FitsPile(card, player.Hand[^1]);

                target.Hand.Add(card);
                State.DrawnCard = null;
                if (card.Suit != Suit.Spades) State.LastNonSpade = card;
                AddLog(toSelf ? $"{player.Nick} кладе {card} собі" : $"{player.Nick} кладе {card} на {target.Nick}");

                Publish(GameActionKind.Give, player.Id, target.Id, card);

                if (State.Deck.Count == 0) // у колоді закінчились карти: остання визначає козир, і починаємо грати
                {
                    // Піка козирем не буває, тому після пікової карти дивимось на попередню непікову.
                    // Таких у партії завжди більше, ніж усіх пік разом, тож вона точно знайдеться.
                    State.Trump = State.LastNonSpade!;
                    State.SubPhase = InProgressPhase.Playing;
                    AddLog($"Колода закінчилась, козир — {State.Trump}. Першим ходить {player.Nick}");
                    TakeWaitingPenky(); // хто переклав усю купку, той починає гру з пеньків
                    StartNextFoulPenalty();
                }
                else if (toSelf && !fitsOwnPile)
                {
                    NextTurn(); // якщо нікому не підходить, то кладемо собі і передаємо хід
                }
                return null;
            }
        }

        // Перед тим як тягнути карту, верхню карту треба перекласти на першого зліва направо суперника, якому вона підходить.
        // Спробувати можна завжди: якщо це не він, карта лишається на місці, а гравцеві записується провтик.
        public string? MovingTopCard(string playerId, string targetId)
        {
            lock (_lock)
            {
                var error = CheckTurn(playerId, InProgressPhase.Dealing, out var player);
                if (error != null) return error;
                if (State.DrawnCard != null) return "Спершу покладіть витягнуту карту";
                if (player.Hand.Count == 0) return "У вашій купці немає карт";

                var target = FindPlayer(targetId);
                if (target == null) return "Такого гравця немає";
                if (target == player) return "Перекласти карту можна лише на суперника";

                var card = player.Hand[^1];
                if (FirstFitting(player, card) != target)
                {
                    player.Fouls++;
                    AddLog($"{player.Nick} перекладає {card} на {target.Nick} не за правилами: провтик");
                    NoteFoul(player, card, NotFitReason(player, card, target));
                    return null;
                }

                player.Hand.RemoveAt(player.Hand.Count - 1);
                target.Hand.Add(card);
                AddLog($"{player.Nick} перекладає {card} на {target.Nick}");
                Publish(GameActionKind.Move, player.Id, target.Id, card);
                return null;
            }
        }

        private string NotFitReason(Player player, Card card, Player target)
        {
            if (target.Hand.Count == 0) return $"Купка гравця {target.Nick} порожня";
            return GameRules.FitsPile(card, target.Hand[^1])
                ? $"{card} треба першому зліва: {FirstFitting(player, card)!.Nick}"
                : $"{card} не лягає на {target.Hand[^1]}";
        }

        // ---------- 2 етап: гра ----------

        // Перевірка чи можна покласти карту під час 2 етапу гри
        private bool CanPut(Card myCard) =>
            State.Table.Cards.Count == 0 // на порожній стіл — будь-яку
            || GameRules.Beats(myCard, State.Table.Cards[^1], State.Trump?.Suit);

        // Кладемо карту на стіл
        public string? Putting(string playerId, Card card)
        {
            lock (_lock)
            {
                var error = CheckTurn(playerId, InProgressPhase.Playing, out var player);
                if (error != null) return error;
                if (!player.Hand.Contains(card)) return "У вас немає такої карти";
                if (!CanPut(card))
                {
                    var reason = $"{card} не б'є {State.Table.Cards[^1]}";
                    AddLog($"{player.Nick} ходить {card} не за правилами: кожен віддає порушнику карту по поганій");
                    StartPenalty(player, 1);
                    NoteFoul(player, card, reason);
                    return null;
                }

                if (State.Table.Cards.Count == 0) // нове коло
                {
                    State.Table.RoundSize = ActivePlayers.Count;
                    State.Table.RoundPuts = 0;
                }

                player.Hand.Remove(card);
                State.Table.Cards.Add(card);
                State.Table.RoundPuts++;
                AddLog($"{player.Nick} кладе {card}");

                FinishIfNoCards(player);
                var closedRound = RoundIsOver ? CloseRound() : null;

                Publish(GameActionKind.Put, player.Id, null, card, closedRound);

                if (TryFinishGame()) return null;

                // Хто закрив коло, той і ходить далі (пеньки він уже взяв, а якщо їх не було — вийшов)
                if (closedRound == null || !CanMove(player)) NextTurn();
                return null;
            }
        }

        // Коло закрите, коли покладено всі його карти. Якщо ж карт у руках ні в кого не лишилось
        // (буває після передач по поганій), чекати більше нічого.
        private bool RoundIsOver =>
            State.Table.RoundPuts >= State.Table.RoundSize || !State.Players.Any(CanMove);

        private List<Card> CloseRound()
        {
            var closed = new List<Card>(State.Table.Cards);
            State.DiscardCount += closed.Count;
            State.Table.Cards.Clear();
            AddLog("Коло закрито, карти йдуть у відбій");
            TakeWaitingPenky();
            return closed;
        }

        // Рука спорожніла, а пеньків немає: гравець вийшов із гри.
        // Якщо пеньки є, рука лишається порожньою до кінця кола (див. TakeWaitingPenky).
        private void FinishIfNoCards(Player player)
        {
            if (player.Hand.Count > 0 || player.Penky.Count > 0) return;

            player.Place = ++State.FinishedCount;
            AddLog($"{player.Nick} позбувається всіх карт — {player.Place} місце");
        }

        // Коло закінчилось: усі, у кого порожні руки, беруть свої пеньки
        private void TakeWaitingPenky()
        {
            foreach (var player in State.Players.Where(p => p.IsActive && p.Hand.Count == 0 && p.Penky.Count > 0))
            {
                player.Hand.AddRange(player.Penky);
                player.Penky.Clear();
                AddLog($"{player.Nick} бере пеньки");
            }
        }

        // Беремо в руку найнижчу карту зі столу
        public string? Taking(string playerId)
        {
            lock (_lock)
            {
                var error = CheckTurn(playerId, InProgressPhase.Playing, out var player);
                if (error != null) return error;
                if (State.Table.Cards.Count == 0) return "На столі немає карт";

                var card = State.Table.Cards[0];
                State.Table.Cards.RemoveAt(0);
                player.Hand.Add(card);
                AddLog($"{player.Nick} бере зі столу {card}");

                // Стіл спорожнів: наступний почне нове коло, тож це коло скінчилось
                if (State.Table.Cards.Count == 0) TakeWaitingPenky();

                Publish(GameActionKind.Take, player.Id, null, card);

                NextTurn();
                return null;
            }
        }

        // Кожен хід потрапляє до всіх клієнтів через LastAction, а боти запам'ятовують те саме, що побачила б людина
        private void Publish(
            GameActionKind kind,
            string actorId,
            string? targetId = null,
            Card? card = null,
            List<Card>? closedRound = null,
            string? reason = null)
        {
            State.Version++;
            State.LastAction = new GameAction(State.Version, kind, actorId, targetId, card, closedRound, reason);

            foreach (var bot in State.Players.Where(p => p.Memory != null))
            {
                bot.Memory!.Observe(ViewOfAction(State.LastAction, bot));
            }
        }

        // ---------- Боти ----------

        private static readonly string[] BotNames =
        {
            "Бот Тарас", "Бот Панас", "Бот Мирон", "Бот Ганна", "Бот Марта",
            "Бот Остап", "Бот Назар", "Бот Ярина", "Бот Олена", "Бот Лука"
        };

        // Для рішень бота: кімната йому не потрібна, а все інше він бачить так само, як людина
        private static readonly RoomInfo BotRoom = new("", "", false);

        private string? _botError;

        // Версія гри, на яку бот уже "думає", і коли він зробить хід
        private int _botThinkingOnVersion = -1;
        private DateTime _botMoveAt;

        // На якого бота зараз чекає гра. null — чекаємо на людей або нічого не відбувається.
        private Player? BotToAct()
        {
            if (State.Phase != GamePhase.InProgress) return null;
            if (State.Penalty is { } penalty)
            {
                return penalty.PendingGiverIds.Select(FindPlayer).FirstOrDefault(p => p is { IsBot: true });
            }
            var current = State.Players[State.CurrentPlayerIndex];
            return current.IsBot ? current : null;
        }

        // Викликається кілька разів на секунду. Боти думають якийсь час, щоб за їхніми ходами можна було встежити.
        // Повертає true, якщо бот зробив хід.
        public bool RunBots(DateTime now)
        {
            lock (_lock)
            {
                if (BotToAct() == null) return false;

                if (_botThinkingOnVersion != State.Version)
                {
                    _botThinkingOnVersion = State.Version;
                    _botMoveAt = now + ThinkTime();
                    return false;
                }
                return now >= _botMoveAt && PlayBotMove();
            }
        }

        private TimeSpan ThinkTime()
        {
            int milliseconds;
            bool humansStillPlaying = State.Players.Any(p => !p.IsBot && p.IsActive);
            var last = State.LastAction;

            if (!humansStillPlaying) milliseconds = _rng.Next(200, 350); // людям лишилось дочекатися кінця партії
            else if (last?.ClosedRound != null) milliseconds = _rng.Next(1500, 1900); // нехай закрите коло буде видно
            else if (last?.Kind == GameActionKind.Give && State.SubPhase == InProgressPhase.Playing) milliseconds = _rng.Next(1800, 2300); // щойно став відомий козир
            else if (State.Penalty != null) milliseconds = _rng.Next(500, 900);
            else if (State.SubPhase == InProgressPhase.Dealing) milliseconds = State.DrawnCard == null ? _rng.Next(350, 650) : _rng.Next(600, 1000);
            else milliseconds = _rng.Next(800, 1400);

            return TimeSpan.FromMilliseconds(milliseconds);
        }

        // Один хід того бота, на якого чекає гра. Повертає false, якщо такого немає.
        public bool PlayBotMove()
        {
            lock (_lock)
            {
                var bot = BotToAct();
                if (bot == null) return false;

                var view = BuildView(bot, BotRoom, DateTime.UtcNow);
                var move = BotBrain.Decide(view, bot.BotLevel!.Value, bot.Memory!, _rng);
                var error = ApplyBotMove(bot, move);
                if (error != null)
                {
                    // Такого бути не повинно, але гра не має завмерти через помилку в боті
                    _botError = $"{bot.BotLevel}: {move}: {error}";
                    ApplyBotMove(bot, BotBrain.AnyLegalMove(view));
                }
                return true;
            }
        }

        private string? ApplyBotMove(Player bot, BotMove move) => move.Kind switch
        {
            BotMoveKind.Draw => Drawing(bot.Id),
            BotMoveKind.Place => Dealing(bot.Id, move.TargetId!),
            BotMoveKind.Move => MovingTopCard(bot.Id, move.TargetId!),
            BotMoveKind.Put => Putting(bot.Id, move.Card!),
            BotMoveKind.Take => Taking(bot.Id),
            _ => GivingPenalty(bot.Id, move.Card!)
        };

        // Текст останньої помилки бота (і скидає її)
        public string? TakeBotError()
        {
            lock (_lock)
            {
                var error = _botError;
                _botError = null;
                return error;
            }
        }

        // ---------- Що бачать клієнти ----------

        public RoomSummary GetSummary(RoomInfo info)
        {
            lock (_lock)
            {
                var host = FindPlayer(State.HostId ?? "");
                return new RoomSummary(info.Code, info.Name, host?.Nick ?? "", State.Players.Count, Capacity, State.Phase);
            }
        }

        // Самим ботам кімната не потрібна: коли йдуть усі люди, вона зникає
        public bool IsEmpty
        {
            get
            {
                lock (_lock)
                {
                    return State.Players.All(p => p.IsBot);
                }
            }
        }

        // Кожен підключений гравець отримує власний вигляд гри
        public List<(string ConnectionId, GameView View)> GetViews(RoomInfo info)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                return State.Players
                    .Where(p => p.ConnectionId != null)
                    .Select(p => (p.ConnectionId!, BuildView(p, info, now)))
                    .ToList();
            }
        }

        // Карту по поганій передають закрито: її знають лише двоє
        private static ActionView ViewOfAction(GameAction action, Player me)
        {
            bool hideCard = action.Kind == GameActionKind.PenaltyGive && me.Id != action.ActorId && me.Id != action.TargetId;
            return new ActionView(
                action.Seq,
                action.Kind,
                action.ActorId,
                action.TargetId,
                action.Card == null || hideCard ? null : CardView.From(action.Card),
                action.ClosedRound?.Select(CardView.From).ToList(),
                action.Reason);
        }

        private GameView BuildView(Player me, RoomInfo info, DateTime now)
        {
            bool inProgress = State.Phase == GamePhase.InProgress;
            bool dealing = inProgress && State.SubPhase == InProgressPhase.Dealing;
            bool playing = inProgress && State.SubPhase == InProgressPhase.Playing;
            var current = inProgress ? State.Players[State.CurrentPlayerIndex] : null;
            bool myTurn = current == me;

            var grace = inProgress ? GraceInGame : GraceOutOfGame;
            var players = State.Players.Select(p => new PlayerView(
                p.Id,
                p.Nick,
                p.IsBot || p.ConnectionId != null,
                p.DisconnectedAt is { } at ? Math.Max(0, (int)Math.Ceiling((at + grace - now).TotalSeconds)) : null,
                p.Hand.Count,
                p.Penky.Count,
                dealing && p.Hand.Count > 0 ? CardView.From(p.Hand[^1]) : null,
                p.Place,
                p.Fouls,
                p.BotLevel)).ToList();

            var penalty = State.Penalty;
            bool mustGive = penalty != null && penalty.PendingGiverIds.Contains(me.Id);
            var suggested = mustGive ? GetSuggestedGives(me) : new List<Card>();
            var hand = playing
                ? me.Hand.Select(c => new HandCardView(c.Suit, (int)c.Rank, suggested.Contains(c))).ToList()
                : new List<HandCardView>();

            var lastView = State.LastAction == null ? null : ViewOfAction(State.LastAction, me);

            return new GameView(
                info,
                Capacity,
                State.Version,
                State.Phase,
                State.SubPhase,
                me.Id,
                State.HostId,
                players,
                current?.Id,
                State.Deck.Count,
                State.DiscardCount,
                State.DrawnCard == null ? null : CardView.From(State.DrawnCard),
                penalty == null ? null : new PenaltyView(penalty.ViolatorId, penalty.PendingGiverIds.ToList()),
                State.Trump == null ? null : CardView.From(State.Trump),
                State.Table.Cards.Select(CardView.From).ToList(),
                State.Table.RoundSize,
                State.Table.RoundPuts,
                hand,
                dealing && myTurn && State.DrawnCard == null && penalty == null,
                playing && myTurn && State.Table.Cards.Count > 0 && penalty == null,
                State.LoserId,
                lastView,
                State.Log.TakeLast(LogLinesForClient).ToList());
        }
    }
}
