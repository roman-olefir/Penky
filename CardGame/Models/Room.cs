using DurakBackend.Services;

namespace DurakBackend.Models
{
    public record RoomInfo(string Code, string Name, bool IsPrivate);

    public class Room
    {
        public RoomInfo Info { get; }
        public GameService Game { get; }

        public Room(RoomInfo info, GameService game)
        {
            Info = info;
            Game = game;
        }
    }
}
