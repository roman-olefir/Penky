namespace DurakBackend.Services
{
    // Ходить за ботів. Кілька разів на секунду дивиться, чи не чекає якась кімната на бота.
    public class BotRunner : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

        private readonly RoomManager _rooms;
        private readonly GameNotifier _notifier;
        private readonly ILogger<BotRunner> _logger;

        public BotRunner(RoomManager rooms, GameNotifier notifier, ILogger<BotRunner> logger)
        {
            _rooms = rooms;
            _notifier = notifier;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var room in _rooms.AllRooms())
                {
                    try
                    {
                        var phaseBefore = room.Game.State.Phase;
                        if (!room.Game.RunBots(DateTime.UtcNow)) continue;

                        var error = room.Game.TakeBotError();
                        if (error != null) _logger.LogError("Бот зробив неможливий хід: {Error}", error);

                        // Не чекаємо на розсилку: повільний телефон в одній кімнаті не має гальмувати ботів в інших.
                        // Порядок не важливий, бо клієнт ігнорує стани зі старою версією.
                        _ = Push(room, room.Game.State.Phase != phaseBefore);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Не вдалося зробити хід за бота");
                    }
                }
            }
        }

        private async Task Push(Models.Room room, bool lobbyChanged)
        {
            try
            {
                await _notifier.PushRoom(room);
                if (lobbyChanged) await _notifier.PushLobby();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не вдалося розіслати хід бота");
            }
        }
    }
}
