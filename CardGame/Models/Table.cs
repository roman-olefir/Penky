namespace DurakBackend.Models
{
    // Стопка карт на столі. Коло закривається, коли на столі лежить стільки карт, скільки гравців ще в грі:
    // узята зі столу карта коло відкочує.
    public class TableState
    {
        public List<Card> Cards { get; set; } = new();
    }
}
