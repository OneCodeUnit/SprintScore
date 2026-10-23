using Microsoft.AspNetCore.SignalR;

namespace SprintScore.Voting;

/// <summary>
/// Хаб голосования. Участник подключается с <c>participantId</c> (GUID из localStorage),
/// ведущий — дополнительно с <c>role=admin</c>.
/// </summary>
public sealed class VotingHub : Hub
{
    /// <summary>Событие: состояние сессии изменилось. Рассылается всем.</summary>
    public const string StateChanged = "StateChanged";

    /// <summary>Событие: личное состояние подключившегося. Отправляется только ему.</summary>
    public const string Sync = "Sync";

    private readonly VotingSession _session;

    public VotingHub(VotingSession session) => _session = session;

    public override async Task OnConnectedAsync()
    {
        var participantId = GetParticipantId();
        if (participantId is null)
        {
            Context.Abort();
            return;
        }

        var self = _session.Connect(participantId, Context.ConnectionId, IsAdmin());
        await Clients.Caller.SendAsync(Sync, self);
        await BroadcastAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _session.Disconnect(Context.ConnectionId);
        await BroadcastAsync();
    }

    /// <summary>Подтвердить оценку.</summary>
    public async Task Vote(int score)
    {
        var participantId = GetParticipantId();
        if (participantId is null || IsAdmin())
        {
            throw new HubException("Голосовать может только участник.");
        }

        if (score is < VotingSession.MinScore or > VotingSession.MaxScore)
        {
            throw new HubException($"Оценка должна быть от {VotingSession.MinScore} до {VotingSession.MaxScore}.");
        }

        if (_session.Vote(participantId, score))
        {
            await BroadcastAsync();
        }
    }

    /// <summary>Снять свой голос и проголосовать заново.</summary>
    public async Task RetractVote()
    {
        var participantId = GetParticipantId();
        if (participantId is not null && _session.RetractVote(participantId))
        {
            await BroadcastAsync();
        }
    }

    /// <summary>Завершить голосование и раскрыть результаты. Только для ведущего.</summary>
    public async Task Reveal()
    {
        RequireAdmin();
        _session.Reveal();
        await BroadcastAsync();
    }

    /// <summary>Сбросить голоса и начать новый раунд. Только для ведущего.</summary>
    public async Task Restart()
    {
        RequireAdmin();
        _session.Restart();
        await BroadcastAsync();
    }

    private Task BroadcastAsync() => Clients.All.SendAsync(StateChanged, _session.Snapshot());

    private string? GetParticipantId()
    {
        var raw = Context.GetHttpContext()?.Request.Query["participantId"].ToString();
        return Guid.TryParse(raw, out var id) ? id.ToString("N") : null;
    }

    private bool IsAdmin() =>
        Context.GetHttpContext()?.Request.Query["role"].ToString() == "admin";

    /// <remarks>
    /// Это не защита: по требованию ведущим становится любой, кто открыл /admin, без аутентификации.
    /// Проверка лишь не даёт странице участника случайно вызвать управляющие методы.
    /// </remarks>
    private void RequireAdmin()
    {
        if (!IsAdmin())
        {
            throw new HubException("Завершать голосование может только ведущий.");
        }
    }
}
