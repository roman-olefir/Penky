namespace DurakBackend.Models
{
    // Стопка карт на столі
    public class TableState
    {
        public List<Card> Cards { get; set; } = new();

        // Скільки карт має лягти на стіл, щоб коло закрилось (гравців у колі на момент першої карти)
        public int RoundSize { get; set; }

        // Скільки карт покладено в цьому колі. Рахуємо покладені, а не ті, що зараз лежать:
        // взята зі столу карта не відкочує коло назад, інакше партії на 4+ гравців можуть не закінчитись ніколи.
        public int RoundPuts { get; set; }
    }
}
