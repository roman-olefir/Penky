using DurakBackend.DTOs;
using DurakBackend.Models;
using DurakBackend.Services;
using Xunit;

namespace CardGame.Tests
{
    public class RoomManagerTests
    {
        private readonly RoomManager _rooms = new();

        private JoinResult Create(string conn = "host", string nick = "Господар", int max = 4, bool isPrivate = false) =>
            _rooms.Create(conn, new CreateRoomRequest(nick, null, max, isPrivate));

        [Fact]
        public void CreateGivesFourDigitCodeAndToken()
        {
            var result = Create();

            Assert.Null(result.Error);
            Assert.Matches("^[0-9]{4}$", result.Code!);
            Assert.NotEmpty(result.Token!);
            Assert.Equal("Кімната Господар", _rooms.ListRooms().Single().Name);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t\n")]
        public void EmptyNickIsRejected(string nick)
        {
            Assert.NotNull(Create(nick: nick).Error);
            Assert.Empty(_rooms.ListRooms());
        }

        [Fact]
        public void LongNickIsTrimmedAndSpacesCollapsed()
        {
            var created = Create(nick: "  Дуже   довгий нікнейм для гравця  ");
            var seat = _rooms.FindSeat("host")!;

            Assert.Null(created.Error);
            Assert.True(seat.Room.Game.State.Players[0].Nick.Length <= RoomManager.NickMaxLength);
            Assert.DoesNotContain("  ", seat.Room.Game.State.Players[0].Nick);
        }

        [Fact]
        public void JoinByCodeWorksAndWrongCodeDoesNot()
        {
            var created = Create();

            Assert.NotNull(_rooms.Join("guest", new JoinRoomRequest("Гість", "nope")).Error);
            Assert.Null(_rooms.Join("guest", new JoinRoomRequest("Гість", $" {created.Code} ")).Error);

            Assert.Equal(2, _rooms.ListRooms().Single().PlayerCount);
        }

        [Fact]
        public void DuplicateNickIsRejectedIgnoringCase()
        {
            var created = Create(nick: "Петро");
            Assert.NotNull(_rooms.Join("guest", new JoinRoomRequest("ПЕТРО", created.Code!)).Error);
        }

        [Fact]
        public void FullRoomRejectsNewPlayers()
        {
            var created = Create(max: 2);
            Assert.Null(_rooms.Join("g1", new JoinRoomRequest("Один", created.Code!)).Error);
            Assert.NotNull(_rooms.Join("g2", new JoinRoomRequest("Два", created.Code!)).Error);
        }

        [Fact]
        public void CannotJoinGameInProgress()
        {
            var created = Create();
            Assert.Null(_rooms.Join("g1", new JoinRoomRequest("Один", created.Code!)).Error);
            var seat = _rooms.FindSeat("host")!;
            Assert.Null(seat.Room.Game.StartGame(seat.PlayerId));

            Assert.NotNull(_rooms.Join("g2", new JoinRoomRequest("Два", created.Code!)).Error);
        }

        [Fact]
        public void ConnectionCannotSitInTwoRooms()
        {
            var first = Create();
            var other = Create(conn: "other", nick: "Інший");

            Assert.NotNull(_rooms.Join("other", new JoinRoomRequest("Інший", first.Code!)).Error);
            Assert.NotNull(_rooms.Create("host", new CreateRoomRequest("Господар", null, 4, false)).Error);
            Assert.NotEqual(first.Code, other.Code);
        }

        [Fact]
        public void PrivateRoomIsHiddenFromListButJoinableByCode()
        {
            var created = Create(isPrivate: true);

            Assert.Empty(_rooms.ListRooms());
            Assert.Null(_rooms.Join("guest", new JoinRoomRequest("Гість", created.Code!)).Error);
        }

        [Fact]
        public void EmptyRoomDisappearsOnLeave()
        {
            Create();
            _rooms.Leave("host");
            Assert.Empty(_rooms.ListRooms());
            Assert.Null(_rooms.FindSeat("host"));
        }

        [Fact]
        public void DisconnectKeepsSeatAndTokenReturnsIt()
        {
            var created = Create();
            var playerId = _rooms.FindSeat("host")!.PlayerId;
            Assert.False(_rooms.CanRejoin(new RejoinRequest(created.Code!, created.Token!))); // поки онлайн, повертатись нікуди

            _rooms.MarkDisconnected("host", DateTime.UtcNow);
            Assert.Null(_rooms.FindSeat("host"));
            Assert.True(_rooms.CanRejoin(new RejoinRequest(created.Code!, created.Token!)));

            var rejoined = _rooms.Rejoin("new", new RejoinRequest(created.Code!, created.Token!), out var room, out var replaced);

            Assert.Null(rejoined.Error);
            Assert.NotNull(room);
            Assert.Null(replaced);
            Assert.Equal(playerId, _rooms.FindSeat("new")!.PlayerId);
        }

        [Fact]
        public void WrongTokenCannotTakeSeat()
        {
            var created = Create();

            Assert.NotNull(_rooms.Rejoin("thief", new RejoinRequest(created.Code!, "guess"), out _, out _).Error);
            Assert.False(_rooms.CanRejoin(new RejoinRequest(created.Code!, "guess")));
            Assert.Null(_rooms.FindSeat("thief"));
        }

        [Fact]
        public void RejoinFromSecondTabReplacesFirst()
        {
            var created = Create();

            var result = _rooms.Rejoin("tab2", new RejoinRequest(created.Code!, created.Token!), out _, out var replaced);

            Assert.Null(result.Error);
            Assert.Equal("host", replaced);
            Assert.Null(_rooms.FindSeat("host"));

            // Запізніле відключення старої вкладки не має скинути нове з'єднання
            Assert.Null(_rooms.MarkDisconnected("host", DateTime.UtcNow));
            Assert.Equal("tab2", _rooms.FindSeat("tab2")!.Room.Game.State.Players[0].ConnectionId);
        }

        [Fact]
        public void SweepRemovesAbandonedRoom()
        {
            Create();
            var t = DateTime.UtcNow;
            _rooms.MarkDisconnected("host", t);

            _rooms.Sweep(t + GameService.GraceOutOfGame - TimeSpan.FromSeconds(1), out var earlyChange);
            Assert.False(earlyChange);
            Assert.Single(_rooms.ListRooms());

            var changed = _rooms.Sweep(t + GameService.GraceOutOfGame, out var lateChange);
            Assert.True(lateChange);
            Assert.Empty(changed);
            Assert.Empty(_rooms.ListRooms());
        }

        [Fact]
        public void HostCanKickButGuestCannot()
        {
            var created = Create();
            Assert.Null(_rooms.Join("guest", new JoinRoomRequest("Гість", created.Code!)).Error);
            var hostId = _rooms.FindSeat("host")!.PlayerId;
            var guestId = _rooms.FindSeat("guest")!.PlayerId;

            Assert.NotNull(_rooms.Kick("guest", hostId, out _, out _));
            Assert.NotNull(_rooms.Kick("host", hostId, out _, out _));

            Assert.Null(_rooms.Kick("host", guestId, out var room, out var target));
            Assert.Equal("guest", target);
            Assert.NotNull(room);
            Assert.Null(_rooms.FindSeat("guest"));
        }
    }
}
