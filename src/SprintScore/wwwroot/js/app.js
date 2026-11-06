// Клиент голосования. Один файл на обе страницы: режим берётся из data-role на <body>.
(function () {
    "use strict";

    if (!["admin", "participant"].includes(document.body.dataset.role)) return;

    var IS_ADMIN = document.body.dataset.role === "admin";
    var STORAGE_KEY = "sprintscore.participantId";

    // Локальное состояние вкладки.
    var selected = null;   // выбрана, но ещё не подтверждена
    var committed = null;  // подтверждённая оценка (пришла с сервера или только что отправлена)
    var round = null;      // номер раунда, к которому относится committed
    var revealed = false;
    var revision = -1;
    var adminDirty = false;
    var edit = 0;
    var serverAdminVote = null;

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
        restart: document.getElementById("restart"),
        adminVote: document.getElementById("admin-vote"),
        adminStatus: document.getElementById("admin-vote-status"),
        adminClear: document.getElementById("admin-vote-clear")
    };

    /** Стабильный идентификатор участника: переживает F5 и объединяет вкладки одного человека. */
    function participantId() {
        var id = localStorage.getItem(STORAGE_KEY);
        if (!id || !/^(?:[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i.test(id)) {
            id = crypto.randomUUID ? crypto.randomUUID() : "10000000-1000-4000-8000-100000000000".replace(/[018]/g, function (c) {
                return (Number(c) ^ (crypto.getRandomValues(new Uint8Array(1))[0] & (15 >> (Number(c) / 4)))).toString(16);
            });
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

    hub.on("Update", function (update) {
        if (update.revision <= revision) return;
        revision = update.revision;
        var state = update.state;
        if (round !== state.round) { selected = null; adminDirty = false; edit++; }
        round = state.round;
        revealed = state.revealed;
        if (IS_ADMIN) {
            serverAdminVote = update.self.myVote;
            if (!adminDirty || revealed) {
                el.adminVote.value = update.self.myVote === null ? "" : String(update.self.myVote);
                updateAdminStatus();
            }
        } else {
            committed = update.self.myVote;
            if (committed !== null) selected = committed;
        }
        applyState(state);
        render();
    });

    hub.onreconnecting(function () { revision = -1; el.banner.hidden = false; });
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
        el.total.textContent = state.totalCount;

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
            el.adminVote.disabled = revealed;
            el.adminClear.disabled = revealed;
            el.hint.textContent = revealed
                ? "Голосование завершено. «Заново» очистит голоса и откроет новый раунд."
                : "Голосование идёт. Вы можете ввести свою оценку.";
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

    function invoke(method, expectedRound, value) {
        var args = [method, expectedRound];
        if (method === "Vote" || method === "SetAdminVote" || method === "Reveal") args.push(value);
        return hub.invoke.apply(hub, args).then(function (result) {
            if (result !== "accepted") throw new Error(result);
        });
    }

    function showError(err) {
        console.error(err);
        el.hint.textContent = "Действие не принято. Проверьте соединение и текущий раунд.";
    }

    function adminValue() {
        var value = el.adminVote.value.trim();
        return /^(10|[1-9])$/.test(value) ? Number(value) : null;
    }

    function updateAdminStatus() {
        var empty = el.adminVote.value.trim() === "";
        var valid = adminValue() !== null;
        el.adminStatus.textContent = empty ? "" : valid ? "Оценка введена" : "Введите целое число от 1 до 10";
        el.adminStatus.classList.toggle("is-error", !empty && !valid);
        el.adminVote.setAttribute("aria-invalid", String(!empty && !valid));
    }

    if (!IS_ADMIN) {
        el.confirm.addEventListener("click", function () {
            if (selected !== null) invoke("Vote", round, selected).catch(showError);
        });
        el.retract.addEventListener("click", function () {
            invoke("RetractVote", round).catch(showError);
        });
    } else {
        var adminQueue = Promise.resolve();
        function enqueue(method, value) {
            var expectedRound = round;
            var expectedEdit = edit;
            adminQueue = adminQueue.catch(function () {}).then(function () {
                if (method === "Reveal" && expectedEdit === edit && !adminDirty) value = adminValue();
                return invoke(method, expectedRound, value);
            });
            adminQueue.then(function () {
                if (round === expectedRound && edit === expectedEdit) {
                    adminDirty = false;
                    if (method === "SetAdminVote") {
                        if (serverAdminVote !== value || adminValue() !== null || el.adminVote.value === "") {
                            el.adminVote.value = serverAdminVote === null ? "" : String(serverAdminVote);
                        }
                        updateAdminStatus();
                        if (serverAdminVote !== null) el.adminStatus.textContent = "Оценка сохранена";
                    }
                }
            }).catch(function (err) {
                showError(err);
                if (round === expectedRound && edit === expectedEdit) {
                    el.adminStatus.textContent = "Не удалось сохранить";
                    el.adminStatus.classList.add("is-error");
                }
            });
        }
        el.adminVote.addEventListener("input", function () {
            adminDirty = true; edit++;
            updateAdminStatus();
            enqueue("SetAdminVote", adminValue());
        });
        el.adminClear.addEventListener("click", function () {
            el.adminVote.value = "";
            adminDirty = true; edit++;
            updateAdminStatus();
            enqueue("SetAdminVote", null);
            el.adminVote.focus();
        });
        el.reveal.addEventListener("click", function () {
            var value = adminValue();
            if (value === null) { el.adminVote.value = ""; updateAdminStatus(); }
            enqueue("Reveal", value);
        });
        el.restart.addEventListener("click", function () { enqueue("Restart"); });
    }

    render();
})();
