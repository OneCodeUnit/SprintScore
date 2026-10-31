using SprintScore.Voting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddSignalR();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<VotingSession>();
builder.Services.AddHostedService<PresenceSweeper>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();

app.MapRazorPages();
app.MapHub<VotingHub>("/hub");

// Проверка живости: HEALTHCHECK в контейнере и мониторинг снаружи.
app.MapGet("/healthz", () => Results.Text("ok"));

app.Run();
