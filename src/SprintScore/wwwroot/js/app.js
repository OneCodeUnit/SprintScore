// Клиент голосования. Один файл на обе страницы: режим берётся из data-role на <body>.
(function () {
    "use strict";

    var IS_ADMIN = document.body.dataset.role === "admin";
    var STORAGE_KEY = "sprintscore.participantId";

    // Локальное состояние вкладки.
    var selected = null;   // выбрана, но ещё не подтверждена
    var committed = null;  // подтверждённая оценка (пришла с сервера или только что отправлена)
    var round = null;      // номер раунда, к которому относится committed
    var revealed = false;

    var el = {
        cards: Array.prototype.slice.call(document.querySelectorAll(".score__card")),
        average: document.getElementById("average"),
        online: document.getElementById("online"),
        voted: document.getElementById("voted"),
        total: document.getElementById("total"),
        pick: document.getElementById("pick"),
        hint: document.getElementById("hint"),
        banner: document.getElementById("reconnect-banner"),
        confirm: document.getElementById("confirm"),
        retract: document.getElementById("retract"),
        reveal: document.getElementById("reveal"),
        restart: document.getElementById("restart")
    };

    /** Стабильный идентификатор участника: переживает F5 и объединяет вкладки одного человека. */
    function participantId() {
        var id = localStorage.getItem(STORAGE_KEY);
        if (!id) {
            id = (crypto.randomUUID ? crypto.randomUUID() : String(Date.now()) + Math.random());
            localStorage.setItem(STORAGE_KEY, id);
        }
        return id;
    }

    function category(score) {
        return score <= 4 ? "low" : score <= 7 ? "mid" : "high";
    }

    // --- Подключение -------------------------------------------------------

    var url = "/hub?participantId=" + encodeURIComponent(participantId()) + (IS_ADMIN ? "&role=admin" : "");

    var hub = new signalR.HubConnectionBuilder()
        .withUrl(url)
        .withAutomaticReconnect()
        .build();

    hub.on("Sync", function (self) {
        round = self.round;
        committed = self.myVote;
        selected = self.myVote;
        render();
    });

    hub.on("StateChanged", function (state) {
        // Ведущий нажал «Заново» — сбрасываем свой выбор.
        if (round !== null && state.round !== round) {
            selected = null;
            committed = null;
        }
        round = state.round;
        revealed = state.revealed;
        applyState(state);
        render();
    });

    hub.onreconnecting(function () { el.banner.hidden = false; });
    hub.onreconnected(function () { el.banner.hidden = true; });
    hub.onclose(function () {
        el.banner.hidden = false;
        el.banner.textContent = "Соединение потеряно. Обновите страницу.";
    });

    hub.start().catch(function (err) {
        console.error(err);
        el.banner.hidden = false;
        el.banner.textContent = "Не удалось подключиться. Обновите страницу.";
    });

    // --- Отрисовка ---------------------------------------------------------

    /** Счётчики и результаты — то, что приходит от сервера. */
    function applyState(state) {
        el.online.textContent = state.onlineCount;
        el.voted.textContent = state.votedCount;
        el.total.textContent = state.onlineCount;

        var distribution = state.distribution;

        el.cards.forEach(function (card) {
            var score = Number(card.dataset.score);
            var tally = document.querySelector('[data-tally="' + score + '"]');
            var count = document.querySelector('[data-count="' + score + '"]');
            var votes = distribution ? distribution[score - 1] : 0;

            tally.replaceChildren();
            for (var i = 0; i < votes; i++) {
                tally.appendChild(document.createElement("i"));
            }
            count.textContent = votes > 0 ? votes : "";
        });

        if (state.average !== null && state.average !== undefined) {
            el.average.textContent = "~ " + state.average.toFixed(2);
            el.average.className = "average average--" + category(state.average);
            el.average.hidden = false;
        } else if (state.revealed) {
            el.average.textContent = "нет голосов";
            el.average.className = "average average--none";
            el.average.hidden = false;
        } else {
            el.average.hidden = true;
        }
    }

    /** Локальный режим страницы: что подсвечено, что нажимается, что написано в подсказке. */
    function render() {
        var active = committed !== null ? committed : selected;

        el.cards.forEach(function (card) {
            var score = Number(card.dataset.score);
            var isActive = !revealed && score === active;
            card.classList.toggle("is-selected", isActive);
            card.classList.toggle("is-committed", isActive && committed !== null);
            card.setAttribute("aria-pressed", String(isActive));
            card.disabled = IS_ADMIN || revealed || committed !== null;
        });

        el.pick.textContent = !revealed && active !== null ? ", " + active : "";

        if (IS_ADMIN) {
            el.reveal.disabled = revealed;
            el.hint.textContent = revealed
                ? "Голосование завершено. «Заново» очистит голоса и откроет новый раунд."
                : "Голосование идёт. Вы не участвуете в нём и не попадаете в счётчик онлайн.";
            return;
        }

        el.confirm.hidden = revealed || committed !== null;
        el.confirm.disabled = selected === null;
        el.retract.hidden = revealed || committed === null;

        el.hint.textContent = revealed
            ? "Голосование завершено."
            : committed !== null
                ? "Ваш голос учтён. Можно переголосовать, пока ведущий не завершил голосование."
                : "Выберите оценку от 1 до 10 и нажмите «Подтвердить».";
    }

    // --- Действия ----------------------------------------------------------

    el.cards.forEach(function (card) {
        card.addEventListener("click", function () {
            if (card.disabled) {
                return;
            }
            selected = Number(card.dataset.score);
            render();
        });
    });

    /** Вызывает метод хаба; onOk выполняется только при успехе. */
    function invoke(method, arg, onOk) {
        var call = arg === undefined ? hub.invoke(method) : hub.invoke(method, arg);
        return call
            .then(function () {
                if (onOk) {
                    onOk();
                }
            })
            .catch(function (err) {
                console.error(err);
                el.hint.textContent = "Не удалось отправить действие. Попробуйте ещё раз.";
            });
    }

    if (!IS_ADMIN) {
        el.confirm.addEventListener("click", function () {
            if (selected === null) {
                return;
            }
            var value = selected;
            invoke("Vote", value, function () {
                committed = value;
                render();
            });
        });

        el.retract.addEventListener("click", function () {
            invoke("RetractVote", undefined, function () {
                committed = null;
                render();
            });
        });
    } else {
        el.reveal.addEventListener("click", function () { invoke("Reveal"); });
        el.restart.addEventListener("click", function () { invoke("Restart"); });
    }

    render();
})();
