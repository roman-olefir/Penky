namespace DurakBackend.Models
{
    public enum GamePhase { WaitingForPlayers, InProgress, Finished }
    public enum InProgressPhase { Dealing, Playing }
    public enum GameActionKind { Draw, Give, Move, Put, Take, Foul, PenaltyGive }

    // Останній хід: за ним клієнт малює анімацію і показує закрите коло
    public record GameAction(
        int Seq,
        GameActionKind Kind,
        string ActorId,
        string? TargetId = null,
        Card? Card = null,
        List<Card>? ClosedRound = null,
        string? Reason = null);

    // Видача "по поганій": гра чекає, поки ці гравці передадуть порушникові карти.
    // Хто винен кілька карт, той записаний стільки ж разів.
    public record PenaltyState(string ViolatorId, List<string> PendingGiverIds);

    public class GameState
    {
        public List<Player> Players { get; set; } = new();
        public string? HostId { get; set; }
        public List<Card> Deck { get; set; } = new();

        // Карта, яку поточний гравець витягнув на 1 етапі, але ще не поклав
        public Card? DrawnCard { get; set; }
        public Card? Trump { get; set; }

        // Остання відкрита непікова карта: за нею визначається козир, бо піка козирем не буває
        public Card? LastNonSpade { get; set; }
        public int CurrentPlayerIndex { get; set; } = 0;
        public TableState Table { get; set; } = new();
        public int DiscardCount { get; set; }

        // Скільки гравців уже позбулося всіх карт (звідси беруться місця)
        public int FinishedCount { get; set; }
        public GamePhase Phase { get; set; } = GamePhase.WaitingForPlayers;
        public InProgressPhase? SubPhase { get; set; }

        // null — ніхто зараз не віддає карту по поганій
        public PenaltyState? Penalty { get; set; }

        // Хто залишився останнім з картами. null — гру перервано (суперники вийшли).
        public string? LoserId { get; set; }
        public GameAction? LastAction { get; set; }

        // Зростає з кожною зміною: клієнт ігнорує стани, що прийшли не по порядку
        public int Version { get; set; }
        public List<string> Log { get; set; } = new();
    }
}
