using DurakBackend.Models;

namespace DurakBackend.DTOs
{
    // Те, що бачить конкретний гравець. Чужі руки, пеньки і колода сюди не потрапляють,
    // тому підглянути їх через інструменти розробника неможливо.
    public record CardView(Suit Suit, int Rank)
    {
        public static CardView From(Card card) => new(card.Suit, (int)card.Rank);
        public Card ToCard() => new(Suit, (Rank)Rank);
    }

    // Suggested — порада, яку карту віддати по поганій (віддати можна будь-яку).
    // Чи можна картою ходити, клієнт не знає: за правилами гравець стежить сам.
    public record HandCardView(Suit Suit, int Rank, bool Suggested)
    {
        public Card ToCard() => new(Suit, (Rank)Rank);
    }

    public record PenaltyView(string ViolatorId, List<string> WaitingFor);

    public record PlayerView(
        string Id,
        string Nick,
        bool IsOnline,
        int? OfflineSecondsLeft,
        int HandCount,
        int PenkyCount,
        CardView? TopCard,
        int? Place,
        int Fouls,
        BotLevel? Bot);

    public record ActionView(
        int Seq,
        GameActionKind Kind,
        string ActorId,
        string? TargetId,
        CardView? Card,
        List<CardView>? ClosedRound,
        string? Reason);

    public record GameView(
        RoomInfo Room,
        int MaxPlayers,
        int Version,
        GamePhase Phase,
        InProgressPhase? SubPhase,
        string MyId,
        string? HostId,
        List<PlayerView> Players,
        string? CurrentPlayerId,
        int DeckCount,
        int DiscardCount,
        CardView? DrawnCard,
        PenaltyView? Penalty,
        CardView? Trump,
        List<CardView> Table,
        int RoundSize,
        int RoundPuts,
        List<HandCardView> Hand,
        bool CanDraw,
        bool CanTake,
        string? LoserId,
        ActionView? LastAction,
        List<string> Log);

    public record RoomSummary(
        string Code,
        string Name,
        string HostNick,
        int PlayerCount,
        int MaxPlayers,
        GamePhase Phase);
}
