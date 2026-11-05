using DurakBackend.DTOs;
using DurakBackend.Models;

namespace DurakBackend.Services
{
    // Що бот пам'ятає про партію. Сюди потрапляє лише те, що бачила б уважна людина на його місці:
    // відкриті карти і дії зі столу. Пеньки і чужі карти, передані закрито, бот не знає.
    public class BotMemory
    {
        private readonly Dictionary<string, HashSet<Card>> _hands = new();
        private readonly HashSet<Card> _discarded = new();

        // Скільки дій уже сталось у партії: за цим бот помічає, що гра затягнулась
        public int ActionsSeen { get; private set; }

        public IReadOnlySet<Card> Discarded => _discarded;

        // Карти, які, наскільки відомо, є в гравця. Після закритої передачі тут може лишитись карта, якої вже немає.
        public IReadOnlySet<Card> KnownHand(string playerId) => HandOf(playerId);

        public void SawOpenCard(string playerId, Card card) => HandOf(playerId).Add(card);

        public void Observe(ActionView action)
        {
            ActionsSeen++;
            var card = action.Card?.ToCard();

            switch (action.Kind)
            {
                case GameActionKind.Give when card != null:
                case GameActionKind.Move when card != null:
                    MoveTo(action.TargetId!, card);
                    break;

                // Спробував походити цією картою: тепер усі знають, що вона в нього.
                // На 1 етапі це витягнута карта, її місце стане відоме, коли вона ляже в купку.
                case GameActionKind.Foul when card != null:
                    MoveTo(action.ActorId, card);
                    break;

                case GameActionKind.Take when card != null:
                    MoveTo(action.ActorId, card);
                    break;

                case GameActionKind.Put when card != null:
                    Forget(card);
                    break;

                // Без карти — передача, якої бот не бачив: відомо лише, що вона була
                case GameActionKind.PenaltyGive when card != null:
                    MoveTo(action.TargetId!, card);
                    break;
            }

            if (action.ClosedRound != null)
            {
                foreach (var closed in action.ClosedRound) _discarded.Add(closed.ToCard());
            }
        }

        private HashSet<Card> HandOf(string playerId)
        {
            if (!_hands.TryGetValue(playerId, out var hand)) _hands[playerId] = hand = new HashSet<Card>();
            return hand;
        }

        private void MoveTo(string playerId, Card card)
        {
            Forget(card);
            HandOf(playerId).Add(card);
        }

        private void Forget(Card card)
        {
            foreach (var hand in _hands.Values) hand.Remove(card);
        }
    }
}
