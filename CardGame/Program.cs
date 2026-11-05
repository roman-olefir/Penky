using System.Text.Json.Serialization;
using DurakBackend.Hubs;
using DurakBackend.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR()
    .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Кімнати живуть у пам'яті, тому менеджер кімнат один на весь сервер
builder.Services.AddSingleton<RoomManager>();
builder.Services.AddSingleton<GameNotifier>();
builder.Services.AddHostedService<RoomSweeper>();
builder.Services.AddHostedService<BotRunner>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHub<GameHub>("/hub/game");

app.Run();
