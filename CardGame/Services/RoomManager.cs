using DurakBackend.DTOs;
using DurakBackend.Models;

namespace DurakBackend.Services
{
    // Усі кімнати живуть у пам'яті сервера: база даних не потрібна,
    // але після перезапуску сервера кімнати зникають.
    public class RoomManager
    {
        public const int MaxRooms = 500;
        public const int NickMaxLength = 16;
        public const int RoomNameMaxLength = 24;

        public record Seat(Room Room, string PlayerId);

        private readonly object _sync = new();
        private readonly Dictionary<string, Room> _rooms = new();
        private readonly Dictionary<string, Seat> _seats = new(); // connectionId -> місце за столом

        public JoinResult Create(string connectionId, CreateRoomRequest request)
        {
            var nick = CleanText(request.Nick, NickMaxLength);
            if (nick.Length == 0) return new JoinResult("Введіть нікнейм");

            var name = CleanText(request.Name, RoomNameMaxLength);
            if (name.Length == 0) name = $"Кімната {nick}";
            var capacity = Math.Clamp(request.MaxPlayers, GameService.MinPlayers, GameService.MaxPlayers);

            lock (_sync)
            {
                if (_seats.ContainsKey(connectionId)) return new JoinResult("Ви вже в кімнаті");
                if (_rooms.Count >= MaxRooms) return new JoinResult("Забагато кімнат, спробуйте пізніше");

                var room = new Room(new RoomInfo(NewCode(), name, request.IsPrivate), new GameService(capacity));
                var player = room.Game.Join(nick, connectionId, out var error);
                if (player == null) return new JoinResult(error);

                _rooms[room.Info.Code] = room;
                _seats[connectionId] = new Seat(room, player.Id);
                return new JoinResult(null, room.Info.Code, player.Token, player.Id);
            }
        }

        public JoinResult Join(string connectionId, JoinRoomRequest request)
        {
            var nick = CleanText(request.Nick, NickMaxLength);
            if (nick.Length == 0) return new JoinResult("Введіть нікнейм");

            lock (_sync)
            {
                if (_seats.ContainsKey(connectionId)) return new JoinResult("Ви вже в кімнаті");
                if (!_rooms.TryGetValue((request.Code ?? "").Trim(), out var room))
                    return new JoinResult("Кімнату з таким кодом не знайдено");

                var player = room.Game.Join(nick, connectionId, out var error);
                if (player == null) return new JoinResult(error);

                _seats[connectionId] = new Seat(room, player.Id);
                return new JoinResult(null, room.Info.Code, player.Token, player.Id);
            }
        }

        // Повернення за стіл після обриву зв'язку. replacedConnectionId — стара вкладка, яку треба відключити.
        public JoinResult Rejoin(string connectionId, RejoinRequest request, out Room? room, out string? replacedConnectionId)
        {
            room = null;
            replacedConnectionId = null;

            lock (_sync)
            {
                if (!_rooms.TryGetValue(request.Code ?? "", out var found))
                    return new JoinResult("Цієї кімнати вже немає");

                var player = found.Game.Reconnect(request.Token ?? "", connectionId, out var previous);
                if (player == null) return new JoinResult("Вашого місця в цій кімнаті вже немає");

                if (previous != null && previous != connectionId)
                {
                    _seats.Remove(previous);
                    replacedConnectionId = previous;
                }
                _seats[connectionId] = new Seat(found, player.Id);
                room = found;
                return new JoinResult(null, found.Info.Code, player.Token, player.Id);
            }
        }

        public bool CanRejoin(RejoinRequest request)
        {
            lock (_sync)
            {
                return _rooms.TryGetValue(request.Code ?? "", out var room) && room.Game.HasOfflineSeat(request.Token ?? "");
            }
        }

        public Seat? FindSeat(string connectionId)
        {
            lock (_sync)
            {
                return _seats.GetValueOrDefault(connectionId);
            }
        }

        // Гравець сам вийшов: місце звільняється одразу
        public Room? Leave(string connectionId)
        {
            lock (_sync)
            {
                if (!_seats.Remove(connectionId, out var seat)) return null;
                seat.Room.Game.Leave(seat.PlayerId);
                RemoveIfEmpty(seat.Room);
                return seat.Room;
            }
        }

        // Зв'язок обірвався: місце тримаємо, гравець може повернутись
        public Room? MarkDisconnected(string connectionId, DateTime now)
        {
            lock (_sync)
            {
                if (!_seats.Remove(connectionId, out var seat)) return null;
                seat.Room.Game.MarkDisconnected(seat.PlayerId, now);
                return seat.Room;
            }
        }

        public string? Kick(string hostConnectionId, string targetId, out Room? room, out string? targetConnectionId)
        {
            room = null;
            targetConnectionId = null;

            lock (_sync)
            {
                if (!_seats.TryGetValue(hostConnectionId, out var seat)) return "Ви не в кімнаті";

                var error = seat.Room.Game.CheckKick(seat.PlayerId, targetId);
                if (error != null) return error;

                targetConnectionId = seat.Room.Game.GetConnectionId(targetId);
                if (targetConnectionId != null) _seats.Remove(targetConnectionId);
                seat.Room.Game.Leave(targetId);
                room = seat.Room;
                return null;
            }
        }

        public List<RoomSummary> ListRooms()
        {
            lock (_sync)
            {
                return _rooms.Values
                    .Where(r => !r.Info.IsPrivate)
                    .Select(r => r.Game.GetSummary(r.Info))
                    .OrderBy(r => r.Phase == GamePhase.InProgress)
                    .ThenBy(r => r.Name)
                    .ToList();
            }
        }

        public List<Room> AllRooms()
        {
            lock (_sync)
            {
                return _rooms.Values.ToList();
            }
        }

        // Прибирає гравців, які не повернулись вчасно. Повертає кімнати, у яких ще хтось лишився і щось змінилось.
        public List<Room> Sweep(DateTime now, out bool anyChanged)
        {
            lock (_sync)
            {
                var changed = new List<Room>();
                anyChanged = false;
                foreach (var room in _rooms.Values.ToList())
                {
                    if (!room.Game.RemoveExpired(now)) continue;
                    anyChanged = true;
                    RemoveIfEmpty(room);
                    if (!room.Game.IsEmpty) changed.Add(room);
                }
                return changed;
            }
        }

        private void RemoveIfEmpty(Room room)
        {
            if (room.Game.IsEmpty) _rooms.Remove(room.Info.Code);
        }

        // Чотири цифри: на телефоні такий код набирається найшвидше
        private string NewCode()
        {
            string code;
            do
            {
                code = Random.Shared.Next(1000, 10000).ToString();
            } while (_rooms.ContainsKey(code));
            return code;
        }

        private static string CleanText(string? text, int maxLength)
        {
            var cleaned = string.Join(' ', new string((text ?? "").Where(c => !char.IsControl(c)).ToArray())
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
            return cleaned.Length > maxLength ? cleaned[..maxLength].TrimEnd() : cleaned;
        }
    }
}
