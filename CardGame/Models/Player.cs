using DurakBackend.Services;

namespace DurakBackend.Models
{
    public class Player
    {
        // Публічний ідентифікатор: його бачать усі гравці
        public string Id { get; init; } = null!;

        // Секрет для повернення в гру після обриву зв'язку: знає лише сам гравець
        public string Token { get; init; } = null!;

        // null — гравець зараз офлайн (його місце тримається, див. GameService.RemoveExpired)
        public string? ConnectionId { get; set; }
        public DateTime? DisconnectedAt { get; set; }

        public string Nick { get; set; } = null!;

        // На 1 етапі це відкрита купка (верхня карта — остання), на 2 етапі — карти в руці
        public List<Card> Hand { get; set; } = new();
        public List<Card> Penky { get; set; } = new();

        // Провтики 1 етапу, за які ще не віддали карти: їх віддають на початку 2 етапу
        public int Fouls { get; set; }

        // Місце, яке гравець зайняв, коли позбувся всіх карт. null — ще грає.
        public int? Place { get; set; }

        public bool IsActive => Place == null;

        // null — людина. У бота немає з'єднання, а ходить за нього сам сервер (див. BotRunner).
        public BotLevel? BotLevel { get; init; }
        public bool IsBot => BotLevel != null;

        // Що бот запам'ятав за поточну партію
        public BotMemory? Memory { get; set; }
    }
}
