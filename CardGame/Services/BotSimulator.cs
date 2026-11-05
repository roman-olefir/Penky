using System.Numerics;
using DurakBackend.DTOs;
using DurakBackend.Models;

namespace DurakBackend.Services
{
    // Як думає складний бот на 2 етапі: кілька десятків разів уявляє, де можуть бути невідомі карти
    // (чужі закриті карти і пеньки, зокрема свої), доігрує кожен можливий хід до кінця партії
    // і обирає той, після якого програє найрідше. Справжніх закритих карт він не бачить.
    //
    // Це окрема швидка модель 2 етапу: карти в ній — біти числа, а не об'єкти, тому тисячі уявних ходів
    // займають частки мілісекунди. Якщо зміняться правила ходу чи закриття кола в GameService, міняти і тут.
    public sealed class BotSimulator
    {
        private const int MoveLimit = 400;

        // Головне — не програти, але вийти раніше теж краще
        private const double PlaceWeight = 0.5;

        // Для кожного козиря: карти пронумеровані від найдешчої до найціннішої,
        // тому молодший встановлений біт — це завжди найдешча карта
        private sealed class Tables
        {
            public readonly Card[] Cards;
            public readonly Dictionary<Card, int> Numbers = new();
            public readonly ulong[] BeatenBy = new ulong[36];

            public Tables(Suit trump)
            {
                Cards = GameRules.FullDeck
                    .OrderBy(c => c.Suit == trump)
                    .ThenBy(c => c.Rank)
                    .ThenBy(c => c.Suit)
                    .ToArray();
                for (int i = 0; i < Cards.Length; i++) Numbers[Cards[i]] = i;

                for (int top = 0; top < Cards.Length; top++)
                {
                    for (int card = 0; card < Cards.Length; card++)
                    {
                        if (GameRules.Beats(Cards[card], Cards[top], trump)) BeatenBy[top] |= 1UL << card;
                    }
                }
            }
        }

        private static readonly Tables[] AllTables =
            Enum.GetValues<Suit>().OrderBy(s => (int)s).Select(s => new Tables(s)).ToArray();

        private readonly GameView _view;
        private readonly Tables _tables;
        private readonly int _count;
        private readonly int _me;
        private ulong _random;

        // Уявлена партія
        private readonly ulong[] _hands;
        private readonly ulong[] _penky;
        private readonly int[] _places;
        private readonly int[] _table = new int[64];
        private int _head, _tail, _roundPuts, _roundSize, _current, _active, _finished;

        // Один варіант розкладу невідомих карт
        private readonly ulong[] _dealtHands;
        private readonly ulong[] _dealtPenky;

        // Справжня позиція, спільна для всіх варіантів
        private readonly int[] _tableStart;
        private readonly ulong[] _knownHands;
        private readonly ulong _unseen;

        public BotSimulator(GameView view, BotMemory memory, Suit trump, Random rng)
        {
            _view = view;
            _tables = AllTables[(int)trump];
            _count = view.Players.Count;
            _me = view.Players.FindIndex(p => p.Id == view.MyId);
            _random = (ulong)rng.NextInt64() | 1;

            _hands = new ulong[_count];
            _penky = new ulong[_count];
            _places = new int[_count];
            _dealtHands = new ulong[_count];
            _dealtPenky = new ulong[_count];
            _tableStart = view.Table.Select(c => _tables.Numbers[c.ToCard()]).ToArray();

            // Що відомо напевно: своя рука, стіл, відбій. Чужі карти — лише тих, хто ще грає:
            // у того, хто вже вийшов, карт немає, тож усе, що про нього пам'ятається, вже застаріло.
            ulong myHand = Mask(view.Hand.Select(c => c.ToCard()));
            ulong seen = myHand | Mask(view.Table.Select(c => c.ToCard())) | Mask(memory.Discarded);
            _knownHands = new ulong[_count];
            for (int i = 0; i < _count; i++)
            {
                if (i != _me && view.Players[i].Place == null) _knownHands[i] = Mask(memory.KnownHand(view.Players[i].Id)) & ~seen;
            }
            _knownHands[_me] = myHand;
            foreach (var known in _knownHands) seen |= known;
            _unseen = ((1UL << 36) - 1) & ~seen;
        }

        // Оцінює можливі ходи й повертає найкращий. take — чи є серед варіантів "взяти карту зі столу".
        public BotMove ChooseMove(List<Card> legal, bool take, int deals)
        {
            var scores = new double[legal.Count + (take ? 1 : 0)];
            for (int deal = 0; deal < deals; deal++)
            {
                Deal();
                for (int option = 0; option < scores.Length; option++)
                {
                    Reset();
                    if (option < legal.Count) Put(_tables.Numbers[legal[option]]);
                    else Take();
                    scores[option] += PlayToEnd();
                }
            }

            int best = 0;
            for (int option = 1; option < scores.Length; option++)
            {
                if (scores[option] > scores[best]) best = option;
            }
            return best < legal.Count ? new BotMove(BotMoveKind.Put, Card: legal[best]) : new BotMove(BotMoveKind.Take);
        }

        private ulong NextRandom()
        {
            _random ^= _random << 13;
            _random ^= _random >> 7;
            _random ^= _random << 17;
            return _random;
        }

        private ulong Mask(IEnumerable<Card> cards)
        {
            ulong mask = 0;
            foreach (var card in cards) mask |= 1UL << _tables.Numbers[card];
            return mask;
        }

        // Розкладає все, що бот не бачив, по чужих руках і пеньках
        private void Deal()
        {
            ulong pool = _unseen;
            for (int i = 0; i < _count; i++)
            {
                _dealtHands[i] = _knownHands[i];
                _dealtPenky[i] = 0;
                if (i == _me || _view.Players[i].Place != null) continue;

                // Після закритих передач пам'ять може містити зайві карти: вони знову стають невідомими
                int extra = BitOperations.PopCount(_dealtHands[i]) - _view.Players[i].HandCount;
                for (; extra > 0; extra--)
                {
                    ulong card = RandomBit(_dealtHands[i]);
                    _dealtHands[i] &= ~card;
                    pool |= card;
                }
            }

            for (int i = 0; i < _count; i++)
            {
                var player = _view.Players[i];
                if (player.Place != null) continue;
                if (i != _me) _dealtHands[i] |= Draw(ref pool, player.HandCount - BitOperations.PopCount(_dealtHands[i]));
                _dealtPenky[i] = Draw(ref pool, player.PenkyCount);
            }
        }

        private ulong Draw(ref ulong pool, int count)
        {
            ulong drawn = 0;
            for (; count > 0 && pool != 0; count--)
            {
                ulong card = RandomBit(pool);
                pool &= ~card;
                drawn |= card;
            }
            return drawn;
        }

        private ulong RandomBit(ulong mask)
        {
            int skip = (int)(NextRandom() % (ulong)BitOperations.PopCount(mask));
            for (; skip > 0; skip--) mask &= mask - 1;
            return mask & (~mask + 1);
        }

        // Повертає партію до справжньої позиції на столі
        private void Reset()
        {
            _head = 0;
            _tail = _tableStart.Length;
            Array.Copy(_tableStart, _table, _tail);
            _roundPuts = _view.RoundPuts;
            _roundSize = _view.RoundSize;
            _current = _me;
            _finished = 0;
            _active = 0;
            for (int i = 0; i < _count; i++)
            {
                _hands[i] = _dealtHands[i];
                _penky[i] = _dealtPenky[i];
                int? place = _view.Players[i].Place;
                _places[i] = place ?? 0;
                if (place != null) _finished++;
                else _active++;
            }
        }

        // Хід переходить тому, хто ще грає і має карти в руці: решта чекає на пеньки до кінця кола
        private void NextTurn()
        {
            for (int i = 0; i < _count; i++)
            {
                _current = (_current + 1) % _count;
                if (_places[_current] == 0 && _hands[_current] != 0) return;
            }
        }

        private bool AnyoneCanMove()
        {
            for (int i = 0; i < _count; i++)
            {
                if (_places[i] == 0 && _hands[i] != 0) return true;
            }
            return false;
        }

        private void TakeWaitingPenky()
        {
            for (int i = 0; i < _count; i++)
            {
                if (_places[i] == 0 && _hands[i] == 0)
                {
                    _hands[i] = _penky[i];
                    _penky[i] = 0;
                }
            }
        }

        private void Put(int card)
        {
            if (_head == _tail)
            {
                _head = _tail = 0;
                _roundSize = _active;
                _roundPuts = 0;
            }

            int player = _current;
            _hands[player] &= ~(1UL << card);
            _table[_tail++] = card;
            _roundPuts++;

            if (_hands[player] == 0 && _penky[player] == 0)
            {
                _places[player] = ++_finished;
                _active--;
            }

            bool closed = _roundPuts >= _roundSize || !AnyoneCanMove();
            if (closed)
            {
                _head = _tail = 0;
                TakeWaitingPenky();
            }
            if (_active > 1 && (!closed || _hands[player] == 0)) NextTurn();
        }

        private void Take()
        {
            _hands[_current] |= 1UL << _table[_head++];
            if (_head == _tail) TakeWaitingPenky();
            NextTurn();
        }

        // Усі гравці в уявній партії грають просто: найдешчою картою, іноді випадковою.
        // Повертає оцінку для бота: 0 — програв, 1 — вийшов першим.
        private double PlayToEnd()
        {
            for (int move = 0; move < MoveLimit; move++)
            {
                if (_places[_me] != 0) return 1 - PlaceWeight * (_places[_me] - 1) / (_count - 1);
                if (_active <= 1) return 0;

                ulong options = _hands[_current];

                // Ходити нікому, лише якщо пам'ять бота розійшлась із реальністю: про такий варіант нічого не скажеш
                if (options == 0) return 0.5;

                if (_head != _tail) options &= _tables.BeatenBy[_table[_tail - 1]];

                if (options == 0)
                {
                    Take();
                    continue;
                }

                ulong card = (NextRandom() & 15) == 0 ? RandomBit(options) : options & (~options + 1);
                Put(BitOperations.TrailingZeroCount(card));
            }

            // Партія затягнулась: нічого певного про результат сказати не можна
            return 0.5;
        }
    }
}
