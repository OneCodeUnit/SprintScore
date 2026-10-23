using Microsoft.AspNetCore.SignalR;

namespace SprintScore.Voting;

/// <summary>
/// Периодически вычищает участников, которые закрыли страницу и не вернулись,
/// и рассылает обновлённые счётчики. Перезагрузка страницы укладывается в отсрочку
/// <see cref="Grace"/> и потому остаётся незаметной.
/// </summary>
public sealed class PresenceSweeper : BackgroundService
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly VotingSession _session;
    private readonly IHubContext<VotingHub> _hub;
    private readonly TimeProvider _time;

    public PresenceSweeper(VotingSession session, IHubContext<VotingHub> hub, TimeProvider time)
    {
        _session = session;
        _hub = hub;
        _time = time;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _time);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (_session.SweepDepartures(Grace))
            {
                await _hub.Clients.All.SendAsync(VotingHub.StateChanged, _session.Snapshot(), stoppingToken);
            }
        }
    }
}
