using DurakBackend.Models;

namespace DurakBackend.Services
{
    // Самі правила карт. Ними користуються і гра, і боти.
    public static class GameRules
    {
        public static readonly IReadOnlyList<Card> FullDeck =
            Enum.GetValues<Suit>().SelectMany(suit => Enum.GetValues<Rank>().Select(rank => new Card(suit, rank))).ToList();

        // 1 етап: карта лягає на купку, якщо вона на один ранг старша. Шістка лягає на туза, масть не важлива.
        public static bool FitsPile(Card card, Card top)
        {
            bool isNextRank = (int)card.Rank == (int)top.Rank + 1;
            bool sixOnAce = top.Rank == Rank.Ace && card.Rank == Rank.Six;
            return isNextRank || sixOnAce;
        }

        // 2 етап: чи б'є карта верхню карту на столі
        public static bool Beats(Card card, Card top, Suit? trump)
        {
            bool sameSuit = card.Suit == top.Suit;

            // Шістка б'є туза тієї самої масті
            if (sameSuit && card.Rank == Rank.Six && top.Rank == Rank.Ace) return true;

            // Особливе правило: піку можна бити тільки пікою, козир її не бере (сама піка козирем не буває)
            if (top.Suit == Suit.Spades) return sameSuit && card.Rank > top.Rank;

            // Звичайні правила "дурня"
            if (card.Suit == trump && top.Suit != trump) return true;

            return sameSuit && card.Rank > top.Rank;
        }
    }
}
