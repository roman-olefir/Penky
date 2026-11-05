using DurakBackend.Hubs;
using DurakBackend.Models;
using Microsoft.AspNetCore.SignalR;

namespace DurakBackend.Services
{
    // Розсилає клієнтам зміни: кожному гравцеві — його власний вигляд гри
    public class GameNotifier
    {
        public const string LobbyGroup = "lobby";

        private readonly IHubContext<GameHub> _hub;
        private readonly RoomManager _rooms;

        public GameNotifier(IHubContext<GameHub> hub, RoomManager rooms)
        {
            _hub = hub;
            _rooms = rooms;
        }

        public Task PushRoom(Room room) =>
            Task.WhenAll(room.Game.GetViews(room.Info)
                .Select(v => _hub.Clients.Client(v.ConnectionId).SendAsync("State", v.View)));

        public Task PushLobby() =>
            _hub.Clients.Group(LobbyGroup).SendAsync("Rooms", _rooms.ListRooms());

        public Task SendKicked(string connectionId, string message) =>
            _hub.Clients.Client(connectionId).SendAsync("Kicked", message);
    }
}
