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

    /// <summary>participantId → подтверждённая оценка.</summary>
    private readonly Dictionary<string, int> _votes = new();

    /// <summary>participantId → момент потери последнего соединения. См. <see cref="SweepDepartures"/>.</summary>
    private readonly Dictionary<string, DateTimeOffset> _pendingDepartures = new();

    private readonly TimeProvider _time;

    private bool _revealed;
    private int _round = 1;
    private long _revision;

    public string Apply(string action, int expectedRound, string participantId, int? score = null)
    {
        lock (_gate)
        {
            if (expectedRound != _round) return "stale";
            if (action != "Restart" && _revealed) return "closed";
            if ((action == "Vote" && !score.HasValue) || (score.HasValue && score is < MinScore or > MaxScore)) return "invalid";
            switch (action)
            {
                case "Vote": Vote(participantId, score!.Value); break;
                case "RetractVote": RetractVote(participantId); break;
                case "SetAdminVote": SetOptionalVote(participantId, score); break;
                case "Reveal": SetOptionalVote(participantId, score); Reveal(); break;
                case "Restart": Restart(); break;
                default: return "invalid";
            }
            return "accepted";
        }
    }

    public List<(string Connection, object Update)> Updates()
    {
        lock (_gate)
        {
            var state = Snapshot();
            var revision = ++_revision;
            var updates = new List<(string, object)>();
            foreach (var (connection, owner) in _owners)
                updates.Add((connection, new { state, revision, self = new SelfStateDto(_votes.TryGetValue(owner, out var vote) ? vote : null, _round) }));
            return updates;
        }
    }

    public VotingSession(TimeProvider time) => _time = time;

    /// <summary>
    /// Регистрирует соединение участника или ведущего.
    /// </summary>
    /// <returns>Личное состояние участника: его текущий голос и номер раунда.</returns>
    public SelfStateDto Connect(string participantId, string connectionId, bool isAdmin)
    {
        lock (_gate)
        {
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

    private void SetOptionalVote(string participantId, int? score)
    {
        if (score.HasValue) _votes[participantId] = score.Value;
        else _votes.Remove(participantId);
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
            var scores = _votes.Values;

            if (_revealed)
            {
                distribution = new int[MaxScore];
                foreach (var score in scores)
                {
                    distribution[score - 1]++;
                }

                if (scores.Count > 0)
                {
                    average = Math.Round(scores.Average(), 2, MidpointRounding.AwayFromZero);
                }
            }

            return new VotingStateDto(
                Round: _round,
                Revealed: _revealed,
                OnlineCount: _connections.Count,
                VotedCount: scores.Count,
                // Сохранённые голоса учитываются в общем числе участников, но не в онлайне.
                TotalCount: _connections.Keys.Union(_votes.Keys).Count(),
                Average: average,
                Distribution: distribution);
        }
    }
}
