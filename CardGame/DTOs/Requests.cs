namespace DurakBackend.DTOs
{
    public record CreateRoomRequest(string Nick, string? Name, int MaxPlayers, bool IsPrivate);
    public record JoinRoomRequest(string Nick, string Code);
    public record RejoinRequest(string Code, string Token);

    // Error != null — не вдалося. Token треба зберегти, щоб повернутись після обриву зв'язку.
    public record JoinResult(string? Error, string? Code = null, string? Token = null, string? PlayerId = null);
}
