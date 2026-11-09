'use strict';

(() => {
    const RANK_LABEL = { 6: '6', 7: '7', 8: '8', 9: '9', 10: '10', 11: 'В', 12: 'Д', 13: 'К', 14: 'Т' };
    const RANK_NAME = { 6: 'шістка', 7: 'сімка', 8: 'вісімка', 9: 'дев\'ятка', 10: 'десятка', 11: 'валет', 12: 'дама', 13: 'король', 14: 'туз' };
    const SUIT_NAME = { Hearts: 'чирва', Diamonds: 'бубна', Clubs: 'трефа', Spades: 'піка' };
    const SUIT_ORDER = ['Spades', 'Hearts', 'Clubs', 'Diamonds'];
    const BOT_LEVELS = { Easy: 'легкий', Medium: 'середній', Hard: 'складний' };

    // Розташування значків на картах від 6 до 10: [x%, y%]
    const L = 36, C = 50, R = 64;
    const PIPS = {
        6: [[L, 22], [R, 22], [L, 50], [R, 50], [L, 78], [R, 78]],
        7: [[L, 22], [R, 22], [C, 36], [L, 50], [R, 50], [L, 78], [R, 78]],
        8: [[L, 22], [R, 22], [C, 36], [L, 50], [R, 50], [C, 64], [L, 78], [R, 78]],
        9: [[L, 20], [R, 20], [L, 40], [R, 40], [C, 50], [L, 60], [R, 60], [L, 80], [R, 80]],
        10: [[L, 20], [R, 20], [C, 30], [L, 40], [R, 40], [L, 60], [R, 60], [C, 70], [L, 80], [R, 80]],
    };

    const NICK_KEY = 'penky.nick';
    const SESSION_KEY = 'penky.session';           // sessionStorage: кожна вкладка — окремий гравець
    const LAST_SESSION_KEY = 'penky.lastSession';  // localStorage: щоб повернутись після закриття браузера
    const CLOSING_DELAY = 1300;                    // скільки видно закрите коло
    const FLY_DURATION = 300;
    const FOUL_MESSAGE_TIME = 5000;

    const $ = (selector) => document.querySelector(selector);

    // ---------- Сховище: у приватному режимі може бути недоступне ----------

    const store = {
        get(area, key) {
            try { return JSON.parse(window[area].getItem(key)); } catch { return null; }
        },
        set(area, key, value) {
            try { window[area].setItem(key, JSON.stringify(value)); } catch { /* гра працює і без нього */ }
        },
        remove(area, key) {
            try { window[area].removeItem(key); } catch { /* гра працює і без нього */ }
        },
    };

    const app = {
        state: null,
        session: store.get('sessionStorage', SESSION_KEY),
        rooms: [],
        selected: null,
        pendingJoin: false,
        busy: false,
        movingTop: false, // гравець обрав свою верхню карту, щоб перекласти її на суперника
        closing: null,
        closingTimer: null,
        offlineUntil: {},
    };

    // ---------- Побудова DOM. Лише textContent, тож нікнейм не може вставити HTML ----------

    function h(tag, attrs, ...children) {
        const el = document.createElement(tag);
        for (const [key, value] of Object.entries(attrs || {})) {
            if (value == null || value === false) continue;
            if (key === 'class') el.className = value;
            else if (key === 'style') el.style.cssText = value;
            else if (key === 'dataset') Object.assign(el.dataset, value);
            else if (key.startsWith('on')) el.addEventListener(key.slice(2), value);
            else if (value === true) el.setAttribute(key, '');
            else el.setAttribute(key, value);
        }
        el.append(...children.flat().filter((child) => child != null && child !== false));
        return el;
    }

    function icon(id, className) {
        const NS = 'http://www.w3.org/2000/svg';
        const svg = document.createElementNS(NS, 'svg');
        if (className) svg.setAttribute('class', className);
        svg.setAttribute('aria-hidden', 'true');
        const use = document.createElementNS(NS, 'use');
        use.setAttribute('href', `#${id}`);
        svg.append(use);
        return svg;
    }

    const suitIcon = (suit, className) => icon(suit.toLowerCase(), className);

    const isRed = (suit) => suit === 'Hearts' || suit === 'Diamonds';
    const cardKey = (card) => `${card.suit}-${card.rank}`;
    const cardName = (card) => `${RANK_NAME[card.rank]}, ${SUIT_NAME[card.suit]}`;

    function cardFace(card) {
        const parts = [];
        if (PIPS[card.rank]) {
            for (const [x, y] of PIPS[card.rank]) {
                const pip = suitIcon(card.suit, y > 50 ? 'pip flip' : 'pip');
                pip.style.left = `${x}%`;
                pip.style.top = `${y}%`;
                parts.push(pip);
            }
        } else if (card.rank === 14) {
            parts.push(suitIcon(card.suit, 'pip ace'));
        } else {
            parts.push(h('div', { class: 'court' }, RANK_LABEL[card.rank], suitIcon(card.suit)));
        }
        return parts;
    }

    function cardEl(card, { tag = 'div', mini = false, className = '', attrs = {} } = {}) {
        const classes = `card ${isRed(card.suit) ? 'red' : ''} ${mini ? 'mini' : ''} ${className}`;
        const label = tag === 'button' ? { 'aria-label': cardName(card) } : { role: 'img', 'aria-label': cardName(card) };
        if (mini) {
            return h(tag, { class: classes, ...label, ...attrs },
                h('div', { class: 'mini-face' }, RANK_LABEL[card.rank], suitIcon(card.suit)));
        }
        return h(tag, { class: classes, ...label, ...attrs },
            h('div', { class: 'idx' }, RANK_LABEL[card.rank], suitIcon(card.suit)),
            h('div', { class: 'idx br' }, RANK_LABEL[card.rank], suitIcon(card.suit)),
            cardFace(card));
    }

    const backEl = (mini = false, className = '') => h('div', { class: `card back ${mini ? 'mini' : ''} ${className}` });

    function penkyEl(count) {
        if (count === 0) return h('span', { class: 'penky penky-none', title: 'Пеньків немає' }, 'без пеньків');
        const cards = Array.from({ length: count }, () => h('span', { class: 'penky-card' }));
        return h('span', { class: 'penky', title: `Пеньків: ${count}`, 'aria-label': `Пеньків: ${count}` }, cards);
    }

    // ---------- Сповіщення ----------

    function toast(message, kind = '') {
        const el = h('div', { class: `toast ${kind}` }, message);
        const box = $('#toasts');
        box.append(el);
        while (box.children.length > 3) box.firstChild.remove();
        setTimeout(() => el.remove(), kind === 'big' ? 3500 : 2400);
    }

    // Своє порушення: посеред екрана, щоб одразу кинулось в очі й не закривало суперників
    function foulMessage(reason) {
        const el = h('div', { class: 'toast foul', role: 'alert' }, h('b', null, 'Провтик!'), reason);
        $('#foul-toast').replaceChildren(el);
        setTimeout(() => el.remove(), FOUL_MESSAGE_TIME);
    }

    function shake(el) {
        el.classList.remove('shake');
        void el.offsetWidth; // перезапуск анімації
        el.classList.add('shake');
    }

    // ---------- Зв'язок із сервером ----------

    const connection = new signalR.HubConnectionBuilder()
        .withUrl('/hub/game')
        .withAutomaticReconnect({
            // Телефон може спати довго, тому не здаємось ніколи
            nextRetryDelayInMilliseconds: (ctx) => Math.min(1000 * 2 ** ctx.previousRetryCount, 8000),
        })
        .configureLogging(signalR.LogLevel.Warning)
        .build();

    const banner = (text) => {
        const el = $('#conn-banner');
        el.hidden = !text;
        if (text) el.textContent = text;
    };

    async function startConnection() {
        banner('Підключаємось до сервера…');
        for (;;) {
            try {
                await connection.start();
                break;
            } catch {
                await new Promise((resolve) => setTimeout(resolve, 2000));
            }
        }
        banner(null);
        await afterConnect();
    }

    async function afterConnect() {
        if (app.session) {
            try {
                const result = await connection.invoke('Rejoin', app.session);
                if (!result.error) return;
                toast(result.error, 'error');
            } catch {
                return; // зв'язок знову зник — спробуємо після перепідключення
            }
            dropTabSession();
            app.state = null;
        }
        await openLobby();
    }

    connection.onreconnecting(() => banner('Зв\'язок втрачено. Підключаємось знову…'));
    connection.onreconnected(() => { banner(null); afterConnect(); });
    connection.onclose(() => startConnection());

    connection.on('State', (view) => {
        if (!app.session && !app.pendingJoin) return; // запізнілий стан уже покинутої кімнати
        const prev = app.state;
        if (prev && prev.room.code === view.room.code && view.version < prev.version) return;
        app.state = view;
        onStateChanged(prev, view);
    });

    connection.on('Rooms', (rooms) => {
        app.rooms = rooms;
        if (!app.state) renderRoomList();
    });

    connection.on('Kicked', (message) => {
        dropTabSession();
        app.state = null;
        toast(message, 'error');
        document.querySelectorAll('dialog[open]').forEach((dialog) => dialog.close());
        openLobby();
    });

    // Ходи: один за раз, щоб подвійне натискання не надіслало дві дії
    async function act(method, ...args) {
        if (app.busy) return false;
        app.busy = true;
        try {
            const error = await connection.invoke(method, ...args);
            if (error) toast(error, 'error');
            return !error;
        } catch {
            toast('Немає зв\'язку з сервером', 'error');
            return false;
        } finally {
            app.busy = false;
        }
    }

    // ---------- Сесія ----------

    function adoptSession(result) {
        app.session = { code: result.code, token: result.token };
        store.set('sessionStorage', SESSION_KEY, app.session);
        store.set('localStorage', LAST_SESSION_KEY, app.session);
        history.replaceState(null, '', `?room=${result.code}`);
        $('#resume').hidden = true;
    }

    function dropTabSession() {
        app.session = null;
        store.remove('sessionStorage', SESSION_KEY);
        history.replaceState(null, '', location.pathname);
        app.movingTop = false;
    }

    async function openLobby() {
        render();
        try {
            app.rooms = await connection.invoke('SubscribeLobby');
            renderRoomList();

            const last = store.get('localStorage', LAST_SESSION_KEY);
            if (last) {
                const canRejoin = await connection.invoke('CanRejoin', last);
                $('#resume').hidden = !canRejoin || Boolean(app.state);
                $('#resume-code').textContent = last.code;
            }
        } catch { /* після перепідключення лобі відкриється знову */ }
    }

    function readNick() {
        const input = $('#nick');
        const nick = input.value.trim();
        if (!nick) {
            toast('Спершу введіть нікнейм', 'error');
            shake(input);
            input.focus();
            return null;
        }
        store.set('localStorage', NICK_KEY, nick);
        return nick;
    }

    async function enter(method, request) {
        if (app.pendingJoin) return false;
        app.pendingJoin = true;
        try {
            const result = await connection.invoke(method, request);
            if (result.error) {
                toast(result.error, 'error');
                return false;
            }
            adoptSession(result);
            return true;
        } catch {
            toast('Немає зв\'язку з сервером', 'error');
            return false;
        } finally {
            app.pendingJoin = false;
        }
    }

    async function joinRoom(code) {
        const nick = readNick();
        if (!nick) return;
        await enter('JoinRoom', { nick, code });
    }

    async function leaveRoom() {
        dropTabSession();
        store.remove('localStorage', LAST_SESSION_KEY);
        app.state = null;
        app.closing = null;
        document.querySelectorAll('dialog[open]').forEach((dialog) => dialog.close());
        try { await connection.invoke('LeaveRoom'); } catch { /* сервер прибере гравця сам */ }
        await openLobby();
    }

    async function copyText(text) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            // Буфер обміну працює лише через https: на http (локальна мережа) — старий спосіб
            const area = h('textarea', { style: 'position:fixed;opacity:0' });
            area.value = text;
            document.body.append(area);
            area.select();
            const copied = document.execCommand('copy');
            area.remove();
            return copied;
        }
    }

    async function shareRoom(code) {
        const url = `${location.origin}${location.pathname}?room=${code}`;
        if (navigator.share) {
            try {
                await navigator.share({ title: 'Пеньки', text: `Заходь грати в Пеньки! Код кімнати: ${code}`, url });
                return;
            } catch (error) {
                if (error.name === 'AbortError') return;
            }
        }
        toast(await copyText(url) ? 'Посилання скопійовано' : `Код кімнати: ${code}`);
    }

    // ---------- Реакція на новий стан ----------

    const me = (s) => s.players.find((p) => p.id === s.myId);
    const playerById = (s, id) => s.players.find((p) => p.id === id);
    const isMyTurn = (s) => s.currentPlayerId === s.myId;
    const mustGive = (s) => Boolean(s.penalty?.waitingFor.includes(s.myId));
    const canMove = (s) => isMyTurn(s) && !s.penalty;
    const canPlaceCard = (s) => s.subPhase === 'Dealing' && canMove(s) && Boolean(s.drawnCard);
    const needsMe = (s) => s.phase === 'InProgress' && (mustGive(s) || canMove(s));

    function onStateChanged(prev, s) {
        const action = s.lastAction;
        const isNewAction = action && (!prev || prev.lastAction?.seq !== action.seq);

        if (s.table.length > 0 || s.phase !== 'InProgress') app.closing = null;
        if (isNewAction && action.closedRound) {
            app.closing = action.closedRound;
            clearTimeout(app.closingTimer);
            app.closingTimer = setTimeout(() => {
                app.closing = null;
                if (app.state) renderCenter(app.state);
            }, CLOSING_DELAY);
        }

        if (app.selected && (!needsMe(s) || !s.hand.some((c) => cardKey(c) === app.selected))) app.selected = null;
        if (!s.canDraw) app.movingTop = false;

        const now = Date.now();
        app.offlineUntil = {};
        for (const p of s.players) {
            if (p.offlineSecondsLeft != null) app.offlineUntil[p.id] = now + p.offlineSecondsLeft * 1000;
        }

        render();
        if (isNewAction && prev) animateAction(s, action);
        announce(prev, s, isNewAction ? action : null);

        document.title = !needsMe(s) ? 'Пеньки' : mustGive(s) ? '▶ Віддайте карту — Пеньки' : '▶ Ваш хід — Пеньки';
    }

    function announce(prev, s, action) {
        if (!prev) return;

        if (prev.subPhase === 'Dealing' && s.subPhase === 'Playing') {
            toast(`Колода закінчилась. Козир — ${SUIT_NAME[s.trump.suit]}! Беремо карти в руки`, 'big');
        }
        if (action?.kind === 'Foul') {
            if (action.actorId !== s.myId) toast(`Провтик гравця ${playerById(s, action.actorId)?.nick}`, 'big');
            else foulMessage(action.reason);
        }
        // Коло закінчилось: пеньки беруть одразу всі, хто на них чекав
        if (s.phase === 'InProgress') {
            for (const p of s.players) {
                if (playerById(prev, p.id)?.penkyCount > 0 && p.penkyCount === 0) {
                    toast(p.id === s.myId ? 'Ви берете свої пеньки' : `${p.nick} бере пеньки`);
                }
            }
        }
        if (prev.penalty?.violatorId === s.myId && s.penalty?.violatorId !== s.myId && s.phase === 'InProgress') {
            toast('Усі віддали вам карти');
        }
        // Браузер дозволяє вібрацію лише після першого дотику до сторінки
        if (needsMe(s) && !needsMe(prev) && navigator.userActivation?.hasBeenActive) {
            navigator.vibrate?.(40);
        }
    }

    // ---------- Рендер: вибір екрана ----------

    function render() {
        const s = app.state;
        const screen = !s ? 'lobby' : s.phase === 'WaitingForPlayers' ? 'room' : 'game';
        $('#screen-lobby').hidden = screen !== 'lobby';
        $('#screen-room').hidden = screen !== 'room';
        $('#screen-game').hidden = screen !== 'game';

        if (screen === 'lobby') document.title = 'Пеньки';
        if (screen === 'lobby') renderRoomList();
        if (screen === 'room') renderWaitingRoom(s);
        if (screen === 'game') renderGame(s);
        if ($('#dlg-menu').open && s) renderMenu(s);
    }

    // ---------- Лобі ----------

    function renderRoomList() {
        const list = $('#room-list');
        if (app.rooms.length === 0) {
            list.replaceChildren(h('li', { class: 'room-empty' }, 'Поки немає відкритих кімнат. Створіть першу!'));
            return;
        }
        list.replaceChildren(...app.rooms.map((room) => {
            const inProgress = room.phase === 'InProgress';
            const full = room.playerCount >= room.maxPlayers;
            const status = inProgress ? 'гра йде' : full ? 'заповнена' : `господар ${room.hostNick}`;
            return h('li', { class: 'room-row' },
                h('div', { class: 'room-info' },
                    h('div', { class: 'room-title' }, room.name),
                    h('div', { class: 'room-meta' }, `${room.playerCount} / ${room.maxPlayers} · ${status}`)),
                h('button', {
                    type: 'button',
                    class: 'btn small',
                    disabled: inProgress || full,
                    'aria-label': `Увійти в кімнату ${room.name}`,
                    onclick: () => joinRoom(room.code),
                }, 'Увійти'));
        }));
    }

    // ---------- Кімната перед грою ----------

    function avatarColor(text) {
        let hash = 0;
        for (const ch of text) hash = (hash * 31 + ch.codePointAt(0)) % 360;
        return `hsl(${hash} 45% 34%)`;
    }

    function playerRow(p, s, { allowKick = true } = {}) {
        const amHost = s.hostId === s.myId;
        const letterSource = p.bot ? p.nick.split(' ').pop() : p.nick; // у всіх ботів нік починається зі слова "Бот"
        return h('li', { class: 'player-row' },
            h('span', { class: 'avatar', style: `--c:${avatarColor(p.nick)}`, 'aria-hidden': 'true' }, [...letterSource][0].toUpperCase()),
            h('span', { class: 'player-name' }, p.nick),
            p.id === s.myId && h('span', { class: 'badge' }, 'ви'),
            p.id === s.hostId && h('span', { class: 'badge host' }, 'господар'),
            p.bot && h('span', { class: 'badge bot' }, BOT_LEVELS[p.bot]),
            !p.isOnline && h('span', { class: 'badge off' }, 'офлайн'),
            allowKick && amHost && p.id !== s.myId && h('button', {
                type: 'button',
                class: 'btn ghost small',
                'aria-label': `Прибрати гравця ${p.nick}`,
                onclick: () => act('Kick', p.id),
            }, 'Прибрати'));
    }

    // Кнопки додавання бота: їх бачить лише господар, поки за столом є вільні місця
    function botButtons() {
        return h('div', { class: 'bot-buttons' }, Object.entries(BOT_LEVELS).map(([level, name]) =>
            h('button', {
                type: 'button',
                class: 'btn',
                'aria-label': `Додати бота, рівень: ${name}`,
                onclick: () => act('AddBot', level),
            }, name[0].toUpperCase() + name.slice(1))));
    }

    const canAddBot = (s) => s.hostId === s.myId && s.players.length < s.maxPlayers;

    function renderWaitingRoom(s) {
        $('#room-name').textContent = s.room.isPrivate ? `${s.room.name} · приватна` : s.room.name;
        $('#room-code').textContent = s.room.code;
        $('#room-count').textContent = `${s.players.length} / ${s.maxPlayers}`;

        const rows = s.players.map((p) => playerRow(p, s));
        for (let i = s.players.length; i < s.maxPlayers; i++) {
            rows.push(h('li', { class: 'player-row empty' },
                h('span', { class: 'avatar' }),
                h('span', { class: 'player-name' }, 'Вільне місце')));
        }
        $('#room-players').replaceChildren(...rows);

        $('#room-bots').hidden = !canAddBot(s);
        $('#room-bot-buttons').replaceChildren(botButtons());

        const amHost = s.hostId === s.myId;
        const start = $('#room-start');
        start.hidden = !amHost;
        start.disabled = s.players.length < 2;

        const hostNick = playerById(s, s.hostId)?.nick ?? '';
        $('#room-hint').textContent = !amHost
            ? `Чекаємо, поки ${hostNick} почне гру`
            : s.players.length < 2 ? 'Потрібно щонайменше 2 гравці. Додайте бота або надішліть друзям код.' : '';
    }

    // ---------- Ігровий стіл ----------

    function renderGame(s) {
        const phaseName = s.phase === 'Finished'
            ? 'Гру закінчено'
            : s.subPhase === 'Dealing' ? 'Етап 1 · Набір' : 'Етап 2 · Гра';
        $('#g-phase').replaceChildren(phaseName, h('span', { class: 'phase-room' }, ` · кімната ${s.room.code}`));
        renderSeats(s);
        renderCenter(s);
        renderMe(s);
        renderResult(s);
        renderLog($('#side-log-list'), s);
    }

    function renderLog(list, s) {
        list.replaceChildren(...s.log.map((line) => h('li', null, line)));
        list.scrollTop = list.scrollHeight;
    }

    // Суперники по колу, починаючи з того, хто ходить після мене
    function opponentsInOrder(s) {
        const index = s.players.findIndex((p) => p.id === s.myId);
        return [...s.players.slice(index + 1), ...s.players.slice(0, index)];
    }

    // Карти закінчились посеред кола: пеньки можна взяти лише після нього
    const isWaitingForPenky = (p, s) => s.subPhase === 'Playing' && p.place == null && p.handCount === 0;

    function seatNote(p, s) {
        if (p.place != null && s.phase === 'InProgress') return `${p.place} місце`;
        if (app.offlineUntil[p.id]) return 'офлайн';
        if (s.penalty?.waitingFor.includes(p.id)) return 'віддає карту';
        if (s.penalty?.violatorId === p.id) return 'по поганій';
        if (isWaitingForPenky(p, s)) return 'чекає';
        if (p.id === s.currentPlayerId) return 'ходить';
        return p.bot ? BOT_LEVELS[p.bot] : '';
    }

    // Перекладання у два натискання: спершу своя карта, потім суперник
    function setMovingTop(value) {
        app.movingTop = value;
        renderSeats(app.state);
        renderMe(app.state);
    }

    function moveTopTo(playerId) {
        setMovingTop(false);
        act('MoveTop', playerId);
    }

    function renderSeats(s) {
        // Витягнуту або обрану свою верхню карту можна спробувати покласти будь-кому.
        // Чи підходить карта, гравець вирішує сам.
        const moving = app.movingTop && s.canDraw;
        const pickable = canPlaceCard(s) || moving;
        $('#seats').replaceChildren(...opponentsInOrder(s).map((p) => {
            const classes = ['seat'];
            if (p.id === s.currentPlayerId) classes.push('current');
            if (pickable) classes.push('pickable');
            if (!p.isOnline) classes.push('offline');
            if (p.place != null) classes.push('out');

            const shown = p.topCard ? cardEl(p.topCard, { mini: true }) : backEl(true);
            const anchor = h('span', { class: 'anchor' }, p.handCount > 0 ? shown : h('span', { class: 'card mini', style: 'visibility:hidden' }),
                p.handCount > 0 && h('span', { class: 'count-tag' }, p.handCount),
                p.fouls > 0 && h('span', { class: 'foul-tag', title: `Провтиків: ${p.fouls}`, 'aria-label': `Провтиків: ${p.fouls}` }, `!${p.fouls}`));

            return h(pickable ? 'button' : 'div', {
                class: classes.join(' '),
                dataset: { playerId: p.id },
                type: pickable ? 'button' : null,
                'aria-label': !pickable ? null : moving ? `Перекласти свою верхню карту на гравця ${p.nick}` : `Покласти карту на гравця ${p.nick}`,
                onclick: !pickable ? null : moving ? () => moveTopTo(p.id) : () => act('Give', p.id),
            },
                h('div', { class: 'seat-cards' }, anchor, penkyEl(p.penkyCount)),
                h('div', { class: 'seat-name' }, p.nick),
                h('div', { class: 'seat-note' }, seatNote(p, s) || ' '));
        }));
    }

    function renderCenter(s) {
        const center = $('#center');
        if (s.subPhase === 'Dealing') renderDealingCenter(center, s);
        else renderPlayingCenter(center, s);
    }

    function renderDealingCenter(center, s) {
        const deck = s.deckCount > 0
            ? h(s.canDraw ? 'button' : 'div', {
                id: 'deck',
                type: s.canDraw ? 'button' : null,
                class: `deck ${s.canDraw ? 'can-draw' : ''}`,
                'aria-label': s.canDraw ? `Витягнути карту. У колоді ${s.deckCount}` : `У колоді ${s.deckCount}`,
                onclick: s.canDraw ? () => act('Draw') : null,
            }, backEl(), h('span', { class: 'deck-count' }, s.deckCount))
            : h('div', { id: 'deck', class: 'slot' }, 'Порожньо');

        const wasDrawn = center.dataset.drawn;
        const key = s.drawnCard ? cardKey(s.drawnCard) : '';
        center.dataset.drawn = key;

        const drawn = h('div', { id: 'drawn', class: 'slot' },
            s.drawnCard
                ? cardEl(s.drawnCard, { className: wasDrawn === key ? '' : 'flip-in' })
                : s.canDraw ? 'Натисніть на колоду' : null);

        center.replaceChildren(
            h('div', { class: 'pile fixed' }, deck, h('div', { class: 'pile-label' }, 'Колода')),
            h('div', { class: 'pile fixed' }, drawn, h('div', { class: 'pile-label' }, s.drawnCard ? 'Витягнута карта' : 'Козир — остання карта')));
    }

    function renderPlayingCenter(center, s) {
        center.dataset.drawn = '';
        const cards = s.table.length > 0 ? s.table : (app.closing ?? []);
        const isClosing = s.table.length === 0 && Boolean(app.closing);

        let table;
        if (cards.length === 0) {
            table = h('div', { id: 'table', class: 'slot' });
        } else {
            const stack = h('div', { id: 'table', class: `table-stack ${isClosing ? 'closing' : ''}` });
            const tableWidth = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--tw'));
            const step = tableWidth * .36;
            stack.style.width = `${tableWidth + step * (cards.length - 1)}px`;
            cards.forEach((card, i) => {
                const el = cardEl(card);
                el.style.left = `${i * step}px`;
                stack.append(el);
            });
            table = stack;
        }

        const dots = cards.length > 0
            ? Array.from({ length: s.roundSize }, (_, i) => h('i', { class: i < cards.length ? 'on' : '' }))
            : [];

        center.replaceChildren(
            s.trump && h('div', { class: 'pile trump-pile' },
                cardEl(s.trump, { mini: true }),
                h('div', { class: 'pile-label' }, 'Козир: ', h('b', null, SUIT_NAME[s.trump.suit])),
                h('div', { class: 'pile-label' }, `Відбій: ${s.discardCount}`)),
            h('div', { class: 'pile' },
                table,
                h('div', { class: 'round-dots', 'aria-label': `Коло: на столі ${cards.length} із ${s.roundSize}` }, dots),
                h('div', { class: 'pile-label' }, isClosing ? 'Коло закрито' : 'Стіл')));
    }

    // ---------- Зона гравця ----------

    function offlineWaitText(s) {
        const current = playerById(s, s.currentPlayerId);
        const until = current && app.offlineUntil[current.id];
        if (!until) return null;
        const seconds = Math.max(0, Math.ceil((until - Date.now()) / 1000));
        return `${current.nick} не на зв'язку. Чекаємо ще ${seconds} с`;
    }

    function cardsText(count) {
        if (count === 1) return 'карту';
        return count <= 4 ? `${count} карти` : `${count} карт`;
    }

    function penaltyStatus(s) {
        const violator = playerById(s, s.penalty.violatorId);
        const owedBy = (id) => s.penalty.waitingFor.filter((giver) => giver === id).length;
        if (mustGive(s)) return { text: `Провтик гравця ${violator.nick}: віддайте ${cardsText(owedBy(s.myId))}`, kind: 'mine' };

        const givers = [...new Set(s.penalty.waitingFor)].map((id) => {
            const owed = owedBy(id) > 1 ? ` ×${owedBy(id)}` : '';
            return `${playerById(s, id).nick}${owed}${app.offlineUntil[id] ? ' (не на зв\'язку)' : ''}`;
        }).join(', ');
        return violator.id === s.myId
            ? { text: `Ваш провтик. Карти ще дають: ${givers}`, kind: 'wait' }
            : { text: `Провтик гравця ${violator.nick}. Карти ще дають: ${givers}`, kind: 'wait' };
    }

    function statusInfo(s) {
        const self = me(s);
        if (s.phase !== 'InProgress') return { text: '' };
        if (self.place != null) return { text: `Ви вийшли з гри: ${self.place} місце. Дочекайтесь кінця партії` };
        if (s.penalty) return penaltyStatus(s);
        if (isWaitingForPenky(self, s)) return { text: 'Карти закінчились: пеньки візьмете, коли закінчиться коло' };

        const waiting = offlineWaitText(s);
        if (waiting) return { text: waiting, kind: 'wait' };

        const current = playerById(s, s.currentPlayerId);
        if (!isMyTurn(s)) {
            if (s.subPhase === 'Dealing' && s.drawnCard) return { text: `${current.nick} витягує карту…` };
            return { text: `Хід: ${current.nick}` };
        }

        if (s.subPhase === 'Dealing') {
            if (s.canDraw && app.movingTop) return { text: 'Тепер натисніть на суперника', kind: 'mine' };
            if (s.canDraw) return { text: 'Ваш хід: тягніть карту або перекладіть верхню', kind: 'mine' };
            return { text: 'Оберіть, кому покласти карту', kind: 'mine' };
        }

        if (s.table.length === 0) return { text: 'Ваш хід: покладіть будь-яку карту', kind: 'mine' };
        return { text: 'Ваш хід: побийте карту або візьміть нижню', kind: 'mine' };
    }

    function statusEl(s) {
        const info = statusInfo(s);
        return h('div', { id: 'status', class: `status ${info.kind ?? ''}` }, info.text || ' ');
    }

    function renderMe(s) {
        const root = $('#me');
        const self = me(s);
        root.dataset.playerId = s.myId;

        const info = h('div', { class: 'me-info' },
            h('b', null, self.nick),
            s.subPhase === 'Playing' ? `карт: ${self.handCount}` : null,
            penkyEl(self.penkyCount),
            self.fouls > 0 && h('span', { class: 'foul-note' }, `провтиків: ${self.fouls}`));

        const parts = [statusEl(s)];
        if (s.subPhase === 'Dealing') {
            parts.push(renderMyPile(s, self), info);
        } else if (s.subPhase === 'Playing' && self.place == null) {
            parts.push(info, renderHand(s), renderActions(s));
        } else {
            parts.push(info);
        }
        root.replaceChildren(...parts);
        layoutHand();
    }

    function renderMyPile(s, self) {
        const placing = canPlaceCard(s);
        const choosing = s.canDraw && Boolean(self.topCard);
        const pickable = placing || choosing;
        const shown = self.topCard ? cardEl(self.topCard) : h('div', { class: 'slot' }, 'Порожньо');
        const label = placing ? 'Покласти карту собі' : app.movingTop ? 'Скасувати перекладання' : 'Обрати свою верхню карту, щоб перекласти';
        return h('div', { class: 'my-pile' },
            h(pickable ? 'button' : 'div', {
                class: `target-btn ${pickable ? 'pickable' : ''} ${app.movingTop ? 'moving' : ''}`,
                type: pickable ? 'button' : null,
                'aria-label': pickable ? label : null,
                onclick: placing ? () => act('Give', s.myId) : choosing ? () => setMovingTop(!app.movingTop) : null,
            },
                h('span', { class: 'anchor' }, shown, h('span', { class: 'count-tag' }, self.handCount))));
    }

    function sortHand(hand, trumpSuit) {
        const suitValue = (suit) => (suit === trumpSuit ? 10 : SUIT_ORDER.indexOf(suit));
        return [...hand].sort((a, b) => suitValue(a.suit) - suitValue(b.suit) || a.rank - b.rank);
    }

    // Подвійне натискання потрібне лише на сенсорних екранах: там легко влучити не в ту карту
    const needsConfirm = () => window.matchMedia('(pointer: coarse)').matches;

    function onHandCard(card, s) {
        if (!mustGive(s)) {
            if (s.penalty) {
                toast('Зачекайте: гравці віддають карти по поганій');
                return;
            }
            if (!isMyTurn(s)) {
                toast(`Зараз хід: ${playerById(s, s.currentPlayerId)?.nick}`);
                return;
            }
        }
        const key = cardKey(card);
        if (needsConfirm() && app.selected !== key) {
            app.selected = key;
            renderMe(s);
            return;
        }
        playCard(card);
    }

    function playCard(card) {
        app.selected = null;
        act(mustGive(app.state) ? 'GivePenalty' : 'Put', card.suit, card.rank);
    }

    function renderHand(s) {
        // Підсвічуємо лише пораду, яку карту віддати по поганій. Чим ходити, гравець вирішує сам.
        const cards = sortHand(s.hand, s.trump?.suit).map((card) => {
            const classes = [];
            if (card.suggested) classes.push('suggested');
            if (app.selected === cardKey(card)) classes.push('selected');
            const el = cardEl(card, { tag: 'button', className: classes.join(' '), attrs: { type: 'button', 'data-key': cardKey(card) } });
            el.addEventListener('click', () => onHandCard(card, app.state));
            return el;
        });
        return h('div', { id: 'hand', class: `hand ${needsMe(s) ? 'my-turn' : ''}` }, cards);
    }

    // Рука вміщується в ширину екрана: карти налягають одна на одну, а якщо їх забагато — ідуть у кілька рядів
    function layoutHand() {
        const hand = $('#hand');
        if (!hand || hand.children.length === 0) return;

        const cards = [...hand.children];
        const width = hand.clientWidth;
        const cardWidth = cards[0].offsetWidth;
        const cardHeight = cards[0].offsetHeight;
        const minStep = Math.max(26, cardWidth * .42);
        const maxPerRow = Math.max(1, Math.floor((width - cardWidth) / minStep) + 1);
        const rows = Math.ceil(cards.length / maxPerRow);
        const perRow = Math.ceil(cards.length / rows);
        const rowStep = cardHeight * .56;

        hand.style.height = `${cardHeight + rowStep * (rows - 1)}px`;
        cards.forEach((el, i) => {
            const row = Math.floor(i / perRow);
            const inRow = row < rows - 1 ? perRow : cards.length - perRow * (rows - 1);
            const step = inRow > 1 ? Math.min(cardWidth + 4, (width - cardWidth) / (inRow - 1)) : 0;
            const start = (width - (cardWidth + step * (inRow - 1))) / 2;
            el.style.left = `${start + (i - row * perRow) * step}px`;
            el.style.top = `${row * rowStep}px`;
        });
    }

    function renderActions(s) {
        const selected = s.hand.find((c) => cardKey(c) === app.selected);
        const bottom = s.table[0];
        return h('div', { class: 'actions' },
            s.canTake && h('button', {
                type: 'button',
                class: 'btn',
                onclick: () => { app.selected = null; act('Take'); },
            }, `Взяти ${RANK_LABEL[bottom.rank]} `, suitIcon(bottom.suit)),
            selected && h('button', {
                type: 'button',
                class: 'btn primary',
                onclick: () => playCard(selected),
            }, `${mustGive(s) ? 'Віддати' : 'Покласти'} ${RANK_LABEL[selected.rank]} `, suitIcon(selected.suit)));
    }

    // ---------- Анімація ходів ----------

    function fly(card, fromEl, toEl, hideTarget) {
        if (!fromEl || !toEl || document.hidden || window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;

        const from = fromEl.getBoundingClientRect();
        const to = toEl.getBoundingClientRect();
        if (from.width === 0 || to.width === 0) return;

        const el = card ? cardEl(card) : backEl();
        el.style.setProperty('--w', `${to.width}px`);
        el.style.left = `${to.left}px`;
        el.style.top = `${to.top}px`;
        $('#fly-layer').append(el);

        if (hideTarget) toEl.classList.add('landing');
        const dx = from.left + from.width / 2 - to.left - to.width / 2;
        const dy = from.top + from.height / 2 - to.top - to.height / 2;

        // Якщо старт — це рука чи стопка, карта стартує в розмірі їхньої карти, а не всього блоку
        // Якщо карт там уже немає (поклав останню з руки), летить у своєму розмірі
        const sourceCard = fromEl.matches('.card') ? fromEl : fromEl.querySelector('.card');
        const scale = sourceCard ? sourceCard.getBoundingClientRect().width / to.width : 1;

        const animation = el.animate(
            [{ transform: `translate(${dx}px, ${dy}px) scale(${scale})` }, { transform: 'none' }],
            { duration: FLY_DURATION, easing: 'cubic-bezier(.2, .8, .3, 1)' });
        const finish = () => {
            el.remove();
            toEl.classList.remove('landing');
        };
        animation.onfinish = finish;
        animation.oncancel = finish;
        // Запобіжник: якщо сторінку приспали, анімація може не закінчитись, а карта не має лишатись прихованою
        setTimeout(finish, FLY_DURATION + 200);
    }

    const seatCard = (playerId) => document.querySelector(`#seats [data-player-id="${playerId}"] .anchor .card`);

    // Звідки летить карта: у своїй руці її вже немає, тож стартуємо з руки загалом
    function flightSource(playerId, s) {
        return playerId === s.myId ? $('#hand') : seatCard(playerId);
    }

    // Куди летить карта: завжди одна конкретна карта, бо за її розміром малюється політ
    function flightTarget(playerId, s, card) {
        if (playerId !== s.myId) return seatCard(playerId);
        return document.querySelector(`#hand [data-key="${cardKey(card)}"]`) ?? $('#me .anchor .card');
    }

    function animateAction(s, action) {
        if (action.kind === 'Give') {
            fly(action.card, $('#drawn'), flightTarget(action.targetId, s, action.card), true);
        } else if (action.kind === 'Move') {
            const source = action.actorId === s.myId ? $('#me .anchor') : seatCard(action.actorId);
            fly(action.card, source, flightTarget(action.targetId, s, action.card), true);
        } else if (action.kind === 'Put') {
            fly(action.card, flightSource(action.actorId, s), $('#table .card:last-child'), true);
        } else if (action.kind === 'Take') {
            // У суперника карта лягає поверх сорочки, тож ховати там нічого
            fly(action.card, $('#table'), flightTarget(action.actorId, s, action.card), action.actorId === s.myId);
        } else if (action.kind === 'PenaltyGive') {
            // Чужі бачать лише сорочку
            fly(action.card, flightSource(action.actorId, s), flightTarget(action.targetId, s, action.card), action.targetId === s.myId);
        } else if (action.kind === 'Foul' && action.actorId === s.myId) {
            const el = s.subPhase === 'Dealing' ? $('#drawn .card') : document.querySelector(`#hand [data-key="${cardKey(action.card)}"]`);
            if (el) shake(el);
        }
    }

    // ---------- Кінець гри ----------

    function renderResult(s) {
        const box = $('#result');
        if (s.phase !== 'Finished') {
            box.hidden = true;
            return;
        }
        box.hidden = false;

        const loser = playerById(s, s.loserId);
        const iLost = s.loserId === s.myId;
        const title = !s.loserId
            ? 'Гру перервано'
            : iLost ? 'Ви залишились пеньком!' : `${loser ? loser.nick : 'Хтось'} залишається пеньком!`;
        const subtitle = !s.loserId
            ? 'Суперники вийшли, грати далі нема з ким.'
            : iLost ? 'Цього разу карти були не на вашому боці.' : `Ви посіли ${me(s).place} місце.`;

        const ranked = [...s.players].sort((a, b) => (a.place ?? 99) - (b.place ?? 99));
        const amHost = s.hostId === s.myId;
        const hostNick = playerById(s, s.hostId)?.nick ?? '';

        box.replaceChildren(h('div', { class: 'result-card', role: 'status' },
            icon('stump', 'logo'),
            h('h2', null, title),
            h('p', { class: 'muted' }, subtitle),
            s.playingMoves > 0 && h('p', { class: 'result-moves' }, 'Ходів на другому етапі: ', h('b', null, s.playingMoves)),
            s.loserId && h('ol', { class: 'result-rows' }, ranked.map((p) =>
                h('li', { class: p.id === s.loserId ? 'loser' : '' },
                    h('span', { class: 'place' }, p.place ?? '–'),
                    h('span', { class: 'who' }, p.id === s.myId ? `${p.nick} (ви)` : p.nick)))),
            amHost
                ? h('button', {
                    type: 'button',
                    class: 'btn primary big',
                    disabled: s.players.length < 2,
                    onclick: () => act('StartGame'),
                }, s.players.length < 2 ? `Потрібен ще гравець: код ${s.room.code}` : 'Грати ще раз')
                : h('p', { class: 'muted' }, `Чекаємо, поки ${hostNick} почне нову гру`),
            canAddBot(s) && h('div', { class: 'result-bots' }, h('p', { class: 'muted' }, 'Додати бота'), botButtons()),
            h('button', { type: 'button', class: 'btn big', onclick: leaveRoom }, 'Вийти з кімнати')));
    }

    // ---------- Меню ----------

    function renderMenu(s) {
        $('#menu-title').textContent = `${s.room.name} · код ${s.room.code}`;
        $('#menu-players').replaceChildren(...s.players.map((p) => playerRow(p, s)));
        renderLog($('#menu-log'), s);
    }

    // ---------- Підключення елементів ----------

    function wireUp() {
        document.addEventListener('click', (event) => {
            if (event.target.closest('.rules-btn')) $('#dlg-rules').showModal();
            const closer = event.target.closest('[data-close]');
            if (closer) closer.closest('dialog').close();
        });

        // Натискання поза діалогом закриває його
        document.querySelectorAll('dialog').forEach((dialog) => {
            dialog.addEventListener('click', (event) => {
                if (event.target === dialog) dialog.close();
            });
        });

        const nick = $('#nick');
        nick.value = store.get('localStorage', NICK_KEY) ?? '';
        nick.addEventListener('change', () => {
            if (nick.value.trim()) store.set('localStorage', NICK_KEY, nick.value.trim());
        });

        const code = $('#code');
        code.addEventListener('input', () => {
            code.value = code.value.replace(/\D/g, '');
        });

        const invited = new URLSearchParams(location.search).get('room');
        if (invited && /^\d{4}$/.test(invited)) {
            code.value = invited;
            $('#join-form').classList.add('invited');
            $('#join-label').textContent = 'Вас запросили в кімнату:';
        }

        $('#join-form').addEventListener('submit', (event) => {
            event.preventDefault();
            const value = code.value.trim();
            if (value.length !== 4) {
                toast('Код кімнати — це 4 цифри', 'error');
                shake(code);
                code.focus();
                return;
            }
            joinRoom(value);
        });

        $('#create-btn').addEventListener('click', () => {
            if (readNick()) $('#dlg-create').showModal();
        });

        $('#create-form').addEventListener('submit', async (event) => {
            event.preventDefault();
            const nickValue = readNick();
            if (!nickValue) {
                $('#dlg-create').close();
                return;
            }
            const entered = await enter('CreateRoom', {
                nick: nickValue,
                name: $('#create-name').value,
                maxPlayers: Number(new FormData(event.target).get('max')),
                isPrivate: $('#create-private').checked,
            });
            if (entered) $('#dlg-create').close();
        });

        $('#resume-btn').addEventListener('click', async () => {
            const last = store.get('localStorage', LAST_SESSION_KEY);
            if (!last) return;
            app.session = last;
            store.set('sessionStorage', SESSION_KEY, last);
            history.replaceState(null, '', `?room=${last.code}`);
            $('#resume').hidden = true;
            await afterConnect();
        });

        $('#room-leave').addEventListener('click', leaveRoom);
        $('#room-share').addEventListener('click', () => app.state && shareRoom(app.state.room.code));
        $('#room-start').addEventListener('click', () => act('StartGame'));

        $('#menu-btn').addEventListener('click', () => {
            if (!app.state) return;
            renderMenu(app.state);
            $('#dlg-menu').showModal();
            const log = $('#menu-log');
            log.scrollTop = log.scrollHeight;
        });
        $('#menu-leave').addEventListener('click', () => {
            if (app.state?.phase !== 'InProgress' || confirm('Вийти з гри? Ваші карти вибудуть разом із вами.')) leaveRoom();
        });

        window.addEventListener('resize', () => {
            layoutHand();
            if (app.state && !$('#screen-game').hidden) renderCenter(app.state);
        });

        // Зворотний відлік для гравця, який відключився
        setInterval(() => {
            if (!app.state || Object.keys(app.offlineUntil).length === 0) return;
            const status = $('#status');
            if (!status) return;
            const info = statusInfo(app.state);
            status.textContent = info.text || ' ';
            status.className = `status ${info.kind ?? ''}`;
        }, 1000);
    }

    wireUp();
    render();
    startConnection();
})();
