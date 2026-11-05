using DurakBackend.DTOs;
using DurakBackend.Models;
using DurakBackend.Services;
using Microsoft.AspNetCore.SignalR;

namespace DurakBackend.Hubs
{
    // Події для клієнта:
    //   "State" (GameView)            — новий стан гри, у кожного гравця свій
    //   "Rooms" (List<RoomSummary>)   — оновлення списку кімнат у лобі
    //   "Kicked" (string)             — гравця прибрано з кімнати
    // Методи ходів повертають null або текст помилки.
    public class GameHub : Hub
    {
        private readonly RoomManager _rooms;
        private readonly GameNotifier _notifier;

        public GameHub(RoomManager rooms, GameNotifier notifier)
        {
            _rooms = rooms;
            _notifier = notifier;
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var room = _rooms.MarkDisconnected(Context.ConnectionId, DateTime.UtcNow);
            if (room != null) await _notifier.PushRoom(room);
            await base.OnDisconnectedAsync(exception);
        }

        // ---------- Лобі ----------

        public async Task<List<RoomSummary>> SubscribeLobby()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GameNotifier.LobbyGroup);
            return _rooms.ListRooms();
        }

        public Task UnsubscribeLobby() =>
            Groups.RemoveFromGroupAsync(Context.ConnectionId, GameNotifier.LobbyGroup);

        // ---------- Кімнати ----------

        public async Task<JoinResult> CreateRoom(CreateRoomRequest request)
        {
            var result = _rooms.Create(Context.ConnectionId, request);
            await PushAfterJoin(result);
            return result;
        }

        public async Task<JoinResult> JoinRoom(JoinRoomRequest request)
        {
            var result = _rooms.Join(Context.ConnectionId, request);
            await PushAfterJoin(result);
            return result;
        }

        public async Task<JoinResult> Rejoin(RejoinRequest request)
        {
            var result = _rooms.Rejoin(Context.ConnectionId, request, out var room, out var replaced);
            if (replaced != null) await _notifier.SendKicked(replaced, "Цю гру відкрито на іншій вкладці або пристрої");
            if (room != null)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, GameNotifier.LobbyGroup);
                await _notifier.PushRoom(room);
            }
            return result;
        }

        public bool CanRejoin(RejoinRequest request) => _rooms.CanRejoin(request);

        public async Task LeaveRoom()
        {
            var room = _rooms.Leave(Context.ConnectionId);
            if (room == null) return;
            await _notifier.PushRoom(room);
            await _notifier.PushLobby();
        }

        public async Task<string?> Kick(string playerId)
        {
            var error = _rooms.Kick(Context.ConnectionId, playerId, out var room, out var targetConnection);
            if (error != null) return error;

            if (targetConnection != null) await _notifier.SendKicked(targetConnection, "Господар прибрав вас із кімнати");
            await _notifier.PushRoom(room!);
            await _notifier.PushLobby();
            return null;
        }

        // Прибрати бота можна так само, як і людину: через Kick
        public async Task<string?> AddBot(string level)
        {
            if (!Enum.TryParse<BotLevel>(level, out var parsed) || !Enum.IsDefined(parsed)) return "Невідомий рівень бота";

            var seat = _rooms.FindSeat(Context.ConnectionId);
            if (seat == null) return "Ви не в кімнаті";

            var error = seat.Room.Game.AddBot(seat.PlayerId, parsed);
            if (error != null) return error;

            await _notifier.PushRoom(seat.Room);
            await _notifier.PushLobby();
            return null;
        }

        // ---------- Гра ----------

        public Task<string?> StartGame() => Act((game, playerId) => game.StartGame(playerId));

        // Витягнути карту з колоди (1 етап гри)
        public Task<string?> Draw() => Act((game, playerId) => game.Drawing(playerId));

        // Покласти витягнуту карту гравцеві (1 етап гри)
        public Task<string?> Give(string targetPlayerId) => Act((game, playerId) => game.Dealing(playerId, targetPlayerId));

        // Перекласти свою верхню карту на суперника перед тим, як тягнути (1 етап гри)
        public Task<string?> MoveTop(string targetPlayerId) => Act((game, playerId) => game.MovingTopCard(playerId, targetPlayerId));

        // Покласти карту на стіл (2 етап гри)
        public Task<string?> Put(string cardSuit, int cardRank) =>
            ActWithCard(cardSuit, cardRank, (game, playerId, card) => game.Putting(playerId, card));

        // Взяти карту зі столу (2 етап гри)
        public Task<string?> Take() => Act((game, playerId) => game.Taking(playerId));

        // Віддати карту порушникові правил (по поганій)
        public Task<string?> GivePenalty(string cardSuit, int cardRank) =>
            ActWithCard(cardSuit, cardRank, (game, playerId, card) => game.GivingPenalty(playerId, card));

        private Task<string?> ActWithCard(string cardSuit, int cardRank, Func<GameService, string, Card, string?> action)
        {
            if (!Enum.TryParse<Suit>(cardSuit, out var suit)) return Task.FromResult<string?>("Невідома карта");
            var card = new Card(suit, (Rank)cardRank);
            return Act((game, playerId) => action(game, playerId, card));
        }

        private async Task<string?> Act(Func<GameService, string, string?> action)
        {
            var seat = _rooms.FindSeat(Context.ConnectionId);
            if (seat == null) return "Ви не в кімнаті";

            var game = seat.Room.Game;
            var phaseBefore = game.State.Phase;

            var error = action(game, seat.PlayerId);
            if (error != null) return error;

            await _notifier.PushRoom(seat.Room);
            if (game.State.Phase != phaseBefore) await _notifier.PushLobby();
            return null;
        }

        private async Task PushAfterJoin(JoinResult result)
        {
            if (result.Error != null) return;

            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GameNotifier.LobbyGroup);
            var seat = _rooms.FindSeat(Context.ConnectionId);
            if (seat != null) await _notifier.PushRoom(seat.Room);
            await _notifier.PushLobby();
        }
    }
}
