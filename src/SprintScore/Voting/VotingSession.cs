namespace SprintScore.Voting;

/// <summary>
/// Единственная на всё приложение сессия голосования. Живёт в памяти, регистрируется синглтоном.
/// Все операции короткие (O(1)–O(10)), поэтому защищены одним общим замком.
/// </summary>
public sealed class VotingSession
{
    public const int MinScore = 1;
    public const int MaxScore = 10;

    private readonly Lock _gate = new();

    /// <summary>participantId → его активные соединения (вкладки).</summary>
    private readonly Dictionary<string, HashSet<string>> _connections = new();

    /// <summary>connectionId → participantId. Нужен, чтобы по обрыву связи найти участника.</summary>
    private readonly Dictionary<string, string> _owners = new();
    private readonly HashSet<string> _adminConnections = new();

    /// <summary>participantId → подтверждённая оценка.</summary>
    private readonly Dictionary<string, int> _votes = new();

    /// <summary>participantId → момент потери последнего соединения. См. <see cref="SweepDepartures"/>.</summary>
    private readonly Dictionary<string, DateTimeOffset> _pendingDepartures = new();

    private readonly TimeProvider _time;

    private bool _revealed;
    private int? _adminVote;
    private int _round = 1;

    public VotingSession(TimeProvider time) => _time = time;

    /// <summary>
    /// Регистрирует соединение участника или ведущего.
    /// </summary>
    /// <returns>Личное состояние участника: его текущий голос и номер раунда.</returns>
    public SelfStateDto Connect(string participantId, string connectionId, bool isAdmin)
    {
        lock (_gate)
        {
            if (isAdmin)
            {
                _adminConnections.Add(connectionId);
                return new SelfStateDto(_adminVote, _round);
            }

            if (!_connections.TryGetValue(participantId, out var set))
            {
                _connections[participantId] = set = new HashSet<string>();
            }

            set.Add(connectionId);
            _owners[connectionId] = participantId;
            _pendingDepartures.Remove(participantId);

            return new SelfStateDto(_votes.TryGetValue(participantId, out var vote) ? vote : null, _round);
        }
    }

    /// <summary>
    /// Убирает соединение. Если у участника не осталось ни одной вкладки, он не удаляется сразу,
    /// а попадает в «ожидание ухода»: перезагрузка страницы рвёт связь на секунду, и мигать
    /// счётчиками из-за этого не нужно.
    /// </summary>
    public void Disconnect(string connectionId)
    {
        lock (_gate)
        {
            if (_adminConnections.Remove(connectionId)) return;
            if (!_owners.Remove(connectionId, out var participantId))
            {
                return;
            }

            if (_connections.TryGetValue(participantId, out var set) && set.Remove(connectionId) && set.Count == 0)
            {
                _connections.Remove(participantId);
                _pendingDepartures[participantId] = _time.GetUtcNow();
            }
        }
    }

    /// <summary>Подтверждает оценку участника. Повторный вызов перезаписывает голос.</summary>
    /// <returns><c>true</c>, если состояние изменилось.</returns>
    public bool Vote(string participantId, int score)
    {
        lock (_gate)
        {
            if (_revealed || score is < MinScore or > MaxScore)
            {
                return false;
            }

            if (_votes.TryGetValue(participantId, out var current) && current == score)
            {
                return false;
            }

            _votes[participantId] = score;
            return true;
        }
    }

    /// <summary>Снимает свой голос, чтобы проголосовать заново. После завершения голосования — no-op.</summary>
    /// <returns><c>true</c>, если состояние изменилось.</returns>
    public bool RetractVote(string participantId)
    {
        lock (_gate)
        {
            return !_revealed && _votes.Remove(participantId);
        }
    }

    public bool SetAdminVote(int? score)
    {
        lock (_gate)
        {
            if (_revealed || (score.HasValue && score is < MinScore or > MaxScore) || _adminVote == score)
                return false;
            _adminVote = score;
            return true;
        }
    }

    /// <summary>Завершает голосование и раскрывает результаты. Результаты после этого заморожены.</summary>
    public void Reveal()
    {
        lock (_gate)
        {
            _revealed = true;
        }
    }

    /// <summary>Сбрасывает голоса и открывает новый раунд.</summary>
    public void Restart()
    {
        lock (_gate)
        {
            _votes.Clear();
            _adminVote = null;
            _revealed = false;
            _round++;
        }
    }

    /// <summary>
    /// Окончательно удаляет участников, которые отсутствуют дольше <paramref name="grace"/>,
    /// вместе с их голосами. После завершения голосования голоса сохраняются.
    /// </summary>
    /// <returns><c>true</c>, если кто-то был удалён.</returns>
    public bool SweepDepartures(TimeSpan grace)
    {
        lock (_gate)
        {
            if (_pendingDepartures.Count == 0)
            {
                return false;
            }

            var deadline = _time.GetUtcNow() - grace;
            var gone = _pendingDepartures.Where(x => x.Value <= deadline).Select(x => x.Key).ToList();

            foreach (var participantId in gone)
            {
                _pendingDepartures.Remove(participantId);
                if (!_revealed) _votes.Remove(participantId);
            }

            return gone.Count > 0;
        }
    }

    /// <summary>Собирает текущее состояние для рассылки клиентам.</summary>
    public VotingStateDto Snapshot()
    {
        lock (_gate)
        {
            double? average = null;
            int[]? distribution = null;
            var scores = _adminVote.HasValue ? _votes.Values.Append(_adminVote.Value).ToArray() : _votes.Values.ToArray();

            if (_revealed)
            {
                distribution = new int[MaxScore];
                foreach (var score in scores)
                {
                    distribution[score - 1]++;
                }

                if (scores.Length > 0)
                {
                    average = Math.Round(scores.Average(), 2, MidpointRounding.AwayFromZero);
                }
            }

            return new VotingStateDto(
                Round: _round,
                Revealed: _revealed,
                OnlineCount: _connections.Count + (_adminConnections.Count > 0 ? 1 : 0),
                VotedCount: scores.Length,
                // Сохранённые голоса учитываются в общем числе участников, но не в онлайне.
                TotalCount: _connections.Keys.Union(_pendingDepartures.Keys).Union(_votes.Keys).Count() + (_adminConnections.Count > 0 || _adminVote.HasValue ? 1 : 0),
                Average: average,
                Distribution: distribution);
        }
    }
}
