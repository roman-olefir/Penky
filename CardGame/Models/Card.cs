namespace DurakBackend.Models
{
    public enum Suit { Clubs, Diamonds, Hearts, Spades }
    public enum Rank
    {
        Six = 6, Seven = 7, Eight = 8, Nine = 9, Ten = 10, Jack = 11, Queen = 12, King = 13, Ace = 14
    }

    public record Card(Suit Suit, Rank Rank)
    {
        // Текст для журналу гри. ︎ не дає телефонам малювати масті як емодзі.
        public override string ToString()
        {
            var rank = Rank switch
            {
                Rank.Jack => "В",
                Rank.Queen => "Д",
                Rank.King => "К",
                Rank.Ace => "Т",
                _ => ((int)Rank).ToString()
            };
            var suit = Suit switch
            {
                Suit.Clubs => "♣",
                Suit.Diamonds => "♦",
                Suit.Hearts => "♥",
                _ => "♠"
            };
            return $"{rank}{suit}︎";
        }
    }
}
