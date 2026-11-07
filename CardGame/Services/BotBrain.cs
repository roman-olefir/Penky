using DurakBackend.DTOs;
using DurakBackend.Models;

namespace DurakBackend.Services
{
    public enum BotMoveKind { Draw, Place, Move, Put, Take, Give }

    public record BotMove(BotMoveKind Kind, string? TargetId = null, Card? Card = null);

    // Рішення ботів. Бот отримує той самий GameView, що й людина, плюс власну пам'ять,
    // тож підглянути чужі руки чи пеньки йому просто нізвідки.
    //
    // Сила рівнів заміряна турнірами ботів: на двох легкий програє середньому 4 партії з 5,
    // а середній складному теж 4 з 5. Міняючи числа нижче, варто перевірити це знову (BotTests).
    public static class BotBrain
    {
        // Легкий: часто ходить навмання і порушує правила
        private const double EasyFoulChance = 0.05;
        private const double EasyTakeChance = 0.05;
        private const double EasyLowestCardChance = 0.6;

        // Середній: завжди найменша карта, а випадковість не дає однаковим рішенням зациклити партію
        private const double MediumFoulChance = 0.015;
        private const double MediumRandomChance = 0.05;

        // Складний: скільки варіантів розкладу невідомих карт уявляє перед кожним ходом
        private const int HardDeals = 30;

        // Хто передбачає свій програш, той радо затягував би партію без кінця.
        // Тому дуже довгу партію складний бот догравує простіше.
        private const int LongGameActions = 600;

        public static BotMove Decide(GameView view, BotLevel level, BotMemory memory, Random rng)
        {
            if (view.Penalty?.WaitingFor.Contains(view.MyId) == true) return ChooseGift(view, level, rng);
            if (view.CanDraw) return ChooseBeforeDrawing(view, level, rng);
            if (view.DrawnCard != null) return ChooseTarget(view, level, rng);
            return ChoosePlay(view, level, memory, rng);
        }

        // Запасний хід на випадок помилки в рішенні: завжди законний
        public static BotMove AnyLegalMove(GameView view)
        {
            if (view.Penalty?.WaitingFor.Contains(view.MyId) == true) return Give(view.Hand[0].ToCard());
            if (view.CanDraw) return RequiredMove(view) ?? new BotMove(BotMoveKind.Draw);
            if (view.DrawnCard != null) return new BotMove(BotMoveKind.Place, LegalTargets(view)[0]);
            return view.CanTake ? Take() : Put(view.Hand[0].ToCard());
        }

        private static BotMove Put(Card card) => new(BotMoveKind.Put, Card: card);
        private static BotMove Give(Card card) => new(BotMoveKind.Give, Card: card);
        private static BotMove Take() => new(BotMoveKind.Take);
        private static T Pick<T>(IReadOnlyList<T> items, Random rng) => items[rng.Next(items.Count)];

        private static double FoulChance(BotLevel level) => level switch
        {
            BotLevel.Easy => EasyFoulChance,
            BotLevel.Medium => MediumFoulChance,
            _ => 0
        };

        // ---------- По поганій ----------

        // Легкий віддає що попало, навіть сильну карту. Решта позбуваються найслабшої.
        private static BotMove ChooseGift(GameView view, BotLevel level, Random rng)
        {
            if (level == BotLevel.Easy) return Give(Pick(view.Hand, rng).ToCard());
            return Give(Pick(view.Hand.Where(c => c.Suggested).ToList(), rng).ToCard());
        }

        // ---------- 1 етап ----------

        // Перший зліва направо суперник, якому підходить карта: зліва сидить той, хто ходить наступним
        private static string? FirstFitting(GameView view, Card card)
        {
            int me = view.Players.FindIndex(p => p.Id == view.MyId);
            for (int i = 1; i < view.Players.Count; i++)
            {
                var other = view.Players[(me + i) % view.Players.Count];
                if (other.TopCard != null && GameRules.FitsPile(card, other.TopCard.ToCard())) return other.Id;
            }
            return null;
        }

        private static List<string> LegalTargets(GameView view) =>
            new() { FirstFitting(view, view.DrawnCard!.ToCard()) ?? view.MyId };

        // Верхню карту перекладати обов'язково, якщо вона комусь підходить. Першу роздану карту купки — ніколи.
        private static BotMove? RequiredMove(GameView view)
        {
            var me = view.Players.First(p => p.Id == view.MyId);
            var top = me.HandCount > 1 ? me.TopCard?.ToCard() : null;
            var target = top == null ? null : FirstFitting(view, top);
            return target == null ? null : new BotMove(BotMoveKind.Move, target);
        }

        // Хто помиляється, той часом забуває перекласти карту і тягнеться до колоди: це провтик
        private static BotMove ChooseBeforeDrawing(GameView view, BotLevel level, Random rng)
        {
            var move = RequiredMove(view);
            return move == null || rng.NextDouble() < FoulChance(level) ? new BotMove(BotMoveKind.Draw) : move;
        }

        private static BotMove ChooseTarget(GameView view, BotLevel level, Random rng)
        {
            var legal = LegalTargets(view);

            if (rng.NextDouble() < FoulChance(level))
            {
                var wrong = view.Players.Select(p => p.Id).Except(legal).ToList();
                if (wrong.Count > 0) return new BotMove(BotMoveKind.Place, Pick(wrong, rng));
            }
            return new BotMove(BotMoveKind.Place, legal[0]);
        }

        // ---------- 2 етап ----------

        private static BotMove ChoosePlay(GameView view, BotLevel level, BotMemory memory, Random rng)
        {
            var hand = view.Hand.Select(c => c.ToCard()).ToList();
            var top = view.Table.Count > 0 ? view.Table[^1].ToCard() : null;
            var trump = view.Trump!.Suit;
            var legal = top == null ? hand : hand.Where(c => GameRules.Beats(c, top, trump)).ToList();

            if (rng.NextDouble() < FoulChance(level))
            {
                var illegal = hand.Except(legal).ToList();
                if (illegal.Count > 0) return Put(Pick(illegal, rng));
            }
            if (legal.Count == 0) return Take();

            return level switch
            {
                BotLevel.Easy => PlayEasy(view, legal, rng),
                BotLevel.Medium => PlayMedium(view, legal, trump, rng),
                _ => PlayHard(view, legal, trump, memory, rng)
            };
        }

        // Часом бере карту зі столу, хоч є чим бити, і не береже козирі
        private static BotMove PlayEasy(GameView view, List<Card> legal, Random rng)
        {
            if (view.CanTake && rng.NextDouble() < EasyTakeChance) return Take();
            if (rng.NextDouble() < EasyLowestCardChance) return Put(legal.MinBy(c => c.Rank)!);
            return Put(Pick(legal, rng));
        }

        // Кладе найменшу карту і береже козирі
        private static BotMove PlayMedium(GameView view, List<Card> legal, Suit trump, Random rng)
        {
            if (rng.NextDouble() < MediumRandomChance)
            {
                return view.CanTake && rng.Next(2) == 0 ? Take() : Put(Pick(legal, rng));
            }
            return Put(legal.OrderBy(c => c.Suit == trump).ThenBy(c => c.Rank).First());
        }

        // Пам'ятає всі відкриті карти і доігрує кожен можливий хід подумки (див. BotSimulator)
        private static BotMove PlayHard(GameView view, List<Card> legal, Suit trump, BotMemory memory, Random rng)
        {
            if (legal.Count == 1 && !view.CanTake) return Put(legal[0]);
            if (memory.ActionsSeen > LongGameActions) return PlayMedium(view, legal, trump, rng);
            return new BotSimulator(view, memory, trump, rng).ChooseMove(legal, view.CanTake, HardDeals);
        }
    }
}
