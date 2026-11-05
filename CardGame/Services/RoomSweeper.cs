namespace DurakBackend.Services
{
    // Раз на кілька секунд прибирає гравців, які відключились і не повернулись,
    // щоб гра не зависала на їхньому ході, а порожні кімнати не лишались у пам'яті.
    public class RoomSweeper : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

        private readonly RoomManager _rooms;
        private readonly GameNotifier _notifier;
        private readonly ILogger<RoomSweeper> _logger;

        public RoomSweeper(RoomManager rooms, GameNotifier notifier, ILogger<RoomSweeper> logger)
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
                try
                {
                    var changed = _rooms.Sweep(DateTime.UtcNow, out var anyChanged);
                    foreach (var room in changed) await _notifier.PushRoom(room);
                    if (anyChanged) await _notifier.PushLobby();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Не вдалося прибрати відключених гравців");
                }
            }
        }
    }
}
