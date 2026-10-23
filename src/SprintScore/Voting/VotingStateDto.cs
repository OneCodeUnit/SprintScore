namespace SprintScore.Voting;

/// <summary>
/// Состояние голосования в том виде, в каком оно уходит в браузер.
/// <see cref="Average"/> и <see cref="Distribution"/> заполняются только после
/// завершения голосования — до этого результатов нет даже в сетевом трафике.
/// </summary>
/// <param name="Round">Номер раунда. Меняется при нажатии «Заново» — клиент по этому сбрасывает выбор.</param>
/// <param name="Revealed">Голосование завершено, результаты раскрыты.</param>
/// <param name="OnlineCount">Число участников на странице (ведущий не считается).</param>
/// <param name="VotedCount">Число участников, подтвердивших оценку.</param>
/// <param name="Average">Средняя оценка, округлённая до двух знаков. null, если голосов нет.</param>
/// <param name="Distribution">Число голосов за каждую оценку: индекс 0 — оценка 1, индекс 9 — оценка 10.</param>
public sealed record VotingStateDto(
    int Round,
    bool Revealed,
    int OnlineCount,
    int VotedCount,
    double? Average,
    int[]? Distribution);

/// <summary>Личное состояние участника, которое он получает при подключении.</summary>
/// <param name="MyVote">Подтверждённая оценка этого участника, если он уже голосовал.</param>
/// <param name="Round">Раунд, к которому относится <paramref name="MyVote"/>.</param>
public sealed record SelfStateDto(int? MyVote, int Round);
