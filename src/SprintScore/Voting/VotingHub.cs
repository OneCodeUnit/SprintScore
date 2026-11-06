using Microsoft.AspNetCore.SignalR;

namespace SprintScore.Voting;

public sealed class VotingHub(VotingSession session) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var id = ParticipantId();
        if (id is null) { Context.Abort(); return; }
        session.Connect(id, Context.ConnectionId, IsAdmin());
        await Publish(id => Clients.Client(id), session);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        session.Disconnect(Context.ConnectionId);
        await Publish(id => Clients.Client(id), session);
    }

    public Task<string> Vote(int round, int score) => Change("Vote", round, score, false);
    public Task<string> RetractVote(int round) => Change("RetractVote", round, null, false);
    public Task<string> SetAdminVote(int round, int? score) => Change("SetAdminVote", round, score, true);
    public Task<string> Reveal(int round, int? score) => Change("Reveal", round, score, true);
    public Task<string> Restart(int round) => Change("Restart", round, null, true);

    private async Task<string> Change(string action, int round, int? score, bool admin)
    {
        var id = ParticipantId();
        if (id is null || IsAdmin() != admin) throw new HubException("Действие недоступно для этой роли.");
        var result = session.Apply(action, round, id, score);
        await Publish(id => Clients.Client(id), session);
        return result;
    }

    // Личная оценка отправляется только владельцу; ведущим — только оценка ведущего.
    public static Task Publish(Func<string, IClientProxy> client, VotingSession session) =>
        Task.WhenAll(session.Updates().Select(x => client(x.Connection).SendAsync("Update", x.Update)));

    private string? ParticipantId() => Guid.TryParse(Context.GetHttpContext()?.Request.Query["participantId"], out var id) ? id.ToString("N") : null;
    private bool IsAdmin() => Context.GetHttpContext()?.Request.Query["role"].ToString() == "admin";
}
