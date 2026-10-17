/* Подстрочник — связка интерфейса с нативной частью.
   В приложении работает через WebView2 (window.chrome.webview), в обычном браузере
   включается режим предпросмотра (только с ?demo=1) — для проверки вида без установки. */
(() => {
  const $ = (s) => document.querySelector(s);
  const inApp = !!(window.chrome && window.chrome.webview);
  const demo = !inApp && (new URLSearchParams(location.search).has('demo') || !!window.__forceDemo);

  /* ------------------------- мост ------------------------- */
  function send(obj) {
    if (inApp) window.chrome.webview.postMessage(JSON.stringify(obj));
    else if (demo) mockHandle(obj);
  }
  function onHost(fn) {
    if (!inApp) return;
    window.chrome.webview.addEventListener('message', (ev) => {
      let d = ev.data;
      if (typeof d === 'string') { try { d = JSON.parse(d); } catch (e) { return; } }
      fn(d);
    });
  }

  /* ------------------------- состояние ------------------------- */
  const state = {
    ctxTokens: 0, ctxLimit: 50000, autostart: false, busy: false,
    translation: null,              // {text, at, stats}
    chat: [],                       // [{role:'user'|'bot', text, at}]
  };
  let statusTimer = null;

  const pad = (n) => String(n).padStart(2, '0');
  const fmtTime = (v) => { const d = v ? new Date(v) : new Date(); return pad(d.getHours()) + ':' + pad(d.getMinutes()); };
  const fmtNum = (n) => String(n).replace(/\B(?=(\d{3})+(?!\d))/g, '\u00a0');

  /* ------------------------- язык перевода ------------------------- */
  // Определяем язык вставленного текста по написанию — строка «автоопределение → русский»
  // сразу показывает, с какого языка перевод. Точнее (испанский, немецкий и прочие
  // латинские) эвристика не умеет — для них показывается «английский».
  function detectLang(t) {
    let cyr = 0, lat = 0, han = 0, kana = 0, hangul = 0, arab = 0;
    for (const ch of t || '') {
      const c = ch.codePointAt(0);
      if (c >= 0x0400 && c <= 0x04FF) cyr++;
      else if ((c >= 65 && c <= 90) || (c >= 97 && c <= 122)) lat++;
      else if (c >= 0x4E00 && c <= 0x9FFF) han++;
      else if (c >= 0x3040 && c <= 0x30FF) kana++;
      else if (c >= 0xAC00 && c <= 0xD7AF) hangul++;
      else if (c >= 0x0600 && c <= 0x06FF) arab++;
    }
    if (cyr > lat) return cyr ? 'русский' : '';
    if (lat > cyr) return 'английский';
    if (han) return 'китайский';
    if (kana) return 'японский';
    if (hangul) return 'корейский';
    if (arab) return 'арабский';
    return '';
  }
  function updateLangLine() {
    const lang = detectLang($('#src').value);
    $('#mode-text').textContent = lang === 'русский' ? 'русский'
      : (lang || 'автоопределение') + ' → русский';
  }

  function el(tag, cls, text) {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }

  function setStatus(text, kind) {
    const s = $('#status');
    s.className = 'status-line' + (text ? ' show' : '') + (kind && kind !== 'ok' ? ' ' + kind : '');
    s.textContent = text || '';
    if (statusTimer) { clearTimeout(statusTimer); statusTimer = null; }
    if (text && (!kind || kind === 'ok')) statusTimer = setTimeout(() => { s.className = 'status-line'; s.textContent = ''; }, 7000);
  }

  /* ------------------------- отрисовка ------------------------- */
  function render() {
    // контекст
    const pct = state.ctxLimit ? (state.ctxTokens * 100) / state.ctxLimit : 0;
    $('#ctx-text').textContent = fmtNum(state.ctxTokens) + ' / ' + fmtNum(state.ctxLimit);
    const dot = $('#ctx-dot');
    dot.className = 'dot' + (pct >= 80 ? ' bad' : pct >= 50 ? ' warn' : '');
    // автозапуск
    $('#btn-autostart').classList.toggle('off', !state.autostart);
    // перевод
    const hasTr = !!(state.translation && state.translation.text);
    const trText = $('#tr-text'), trStats = $('#tr-stats');
    trText.textContent = '';
    if (hasTr) {
      trText.textContent = state.translation.text;
      trStats.textContent = state.translation.stats || '';
    } else {
      trText.appendChild(el('span', 'empty', 'Здесь появится перевод'));
      trStats.textContent = '';
    }
    $('#btn-copy').disabled = !hasTr;
    // пока идёт запрос — кнопки действий неактивны
    const b = !!state.busy;
    $('#btn-screen').disabled = b;
    $('#btn-send').disabled = b || !inp.value.trim();
    // сброс контекста — только когда есть что сбрасывать
    $('#btn-reset').disabled = !(state.ctxTokens || state.chat.length || hasTr);
    // галочка в шапке карточки: зелёная только когда перевод есть, иначе приглушённая
    const tick = document.querySelector('.card.translate .card-head svg');
    if (tick) tick.style.stroke = hasTr ? '#a6e3a1' : '#6c7086';
    updateLangLine();
    renderChat();
  }

  function renderChat() {
    const body = $('#chat-body');
    const atBottom = body.scrollHeight - body.scrollTop - body.clientHeight < 40;
    body.textContent = '';
    for (const m of state.chat) {
      if (m.role === 'user') {
        const wrap = el('div', 'msg-user');
        const bub = el('div', 'bubble');
        bub.appendChild(document.createTextNode(m.text));
        const t = el('time', null, fmtTime(m.at));
        bub.appendChild(t);
        wrap.appendChild(bub);
        // innerHTML только со статической SVG-иконкой из макета (данных пользователя здесь нет)
        wrap.appendChild(el('div', 'avatar me')).innerHTML =
          '<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="#cba6f7" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">' +
          '<circle cx="12" cy="8.6" r="3.6"/><path d="M5 19.5c1.2-3.2 3.8-5 7-5s5.8 1.8 7 5"/></svg>';
        body.appendChild(wrap);
      } else {
        const wrap = el('div', 'msg-bot');
        // тоже статическая иконка, без подстановки данных
        wrap.appendChild(el('div', 'avatar bot')).innerHTML =
          '<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="#94e2d5" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">' +
          '<rect x="4" y="8" width="16" height="11" rx="3"/><path d="M12 4.5v3.5M8.5 13h.01M15.5 13h.01"/></svg>';
        const rep = el('div', 'reply');
        rep.appendChild(el('div', 'reply-name', 'Ответ:'));
        const txt = el('div', 'reply-text');
        txt.textContent = m.text;
        txt.appendChild(el('span', 'reply-time', fmtTime(m.at)));
        rep.appendChild(txt);
        wrap.appendChild(rep);
        body.appendChild(wrap);
      }
    }
    if (!state.chat.length) body.appendChild(el('div', 'empty', ''));
    if (atBottom || state.chat.length) body.scrollTop = body.scrollHeight;
  }

  /* ------------------------- действия ------------------------- */
  const src = $('#src'), inp = $('#inp');

  src.addEventListener('input', updateLangLine);
  src.addEventListener('keydown', (e) => {
    // Enter — перевести набранное, Shift+Enter — перенос строки
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send({ cmd: 'translateSource', text: src.value }); }
  });

  function autosize() {
    const field = inp.closest('.compose-field');
    inp.style.height = '32px';
    const need = Math.min(132, Math.max(32, inp.scrollHeight));
    inp.style.height = need + 'px';
    if (field) field.style.height = Math.max(46, need + 14) + 'px';   // 14 = отступы 12 + рамка 2
  }
  inp.addEventListener('input', () => { $('#btn-send').disabled = !inp.value.trim(); autosize(); });
  inp.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); doSend(); }
  });
  function doSend() {
    const text = inp.value.trim();
    if (!text) return;
    send({ cmd: 'chat', text });
    inp.value = '';
    autosize();
    $('#btn-send').disabled = true;
  }
  $('#btn-send').addEventListener('click', doSend);
  $('#btn-copy').addEventListener('click', () => send({ cmd: 'copy' }));
  $('#btn-screen').addEventListener('click', () => send({ cmd: 'screenToInput' }));
  $('#btn-reset').addEventListener('click', () => send({ cmd: 'reset' }));
  $('#btn-autostart').addEventListener('click', () => send({ cmd: 'autostart' }));

  // кнопки окна и перетаскивание за шапку
  document.querySelectorAll('[data-win]').forEach((b) =>
    b.addEventListener('click', () => send({ cmd: 'win', act: b.dataset.win })));
  const bar = document.querySelector('[data-drag]');
  bar.addEventListener('mousedown', (e) => {
    if (e.target.closest('.win-btn')) return;
    if (e.button !== 0) return;
    e.preventDefault();
    send({ cmd: 'drag' });
  });
  bar.addEventListener('dblclick', (e) => { if (!e.target.closest('.win-btn')) send({ cmd: 'win', act: 'max' }); });

  /* Изменение размера: у окна по краям оставлена узкая полоса, за которую окно тянет
     сама Windows (штатный механизм). Из страницы это трогать не нужно — иначе край
     перехватывал бы нажатия по кнопкам в правом верхнем углу. */

  document.addEventListener('keydown', (e) => { if (e.key === 'Escape') send({ cmd: 'hide' }); });

  onHost((m) => {
    if (m.t === 'state' && m.state) {
      Object.assign(state, m.state);
      render();
    } else if (m.t === 'status') {
      setStatus(m.text, m.kind);
    } else if (m.t === 'input') {
      inp.value = m.text || '';
      inp.dispatchEvent(new Event('input'));
      inp.focus();
    } else if (m.t === 'src') {
      src.value = m.text || '';
      src.dispatchEvent(new Event('input'));   // обновит строку языка
    }
  });

  /* ------------------------- режим предпросмотра ------------------------- */
  function mockHandle(obj) {
    if (obj.cmd === 'copy') { setStatus('Текст перевода скопирован (предпросмотр)', 'ok'); return; }
    if (obj.cmd === 'win') { setStatus('предпросмотр: команда окна «' + obj.act + '»', 'ok'); return; }
    if (obj.cmd === 'drag') return;
    if (obj.cmd === 'autostart') { state.autostart = !state.autostart; render(); return; }
    if (obj.cmd === 'reset') { state.translation = null; state.chat = []; state.ctxTokens = 0; render(); setStatus('Контекст очищен (предпросмотр)'); return; }
    if (obj.cmd === 'translateSource' || obj.cmd === 'translateClip') { setStatus('предпросмотр: перевод не выполняется', 'warn'); return; }
    if (obj.cmd === 'chat') { setStatus('предпросмотр: чат не отвечает', 'warn'); return; }
    if (obj.cmd === 'screenToInput') { setStatus('предпросмотр: захват экрана недоступен', 'warn'); return; }
    if (obj.cmd === 'hide') { setStatus('предпросмотр: Esc скрывает окно в трей', 'ok'); return; }
  }

  function demoState() {
    const now = Date.now();
    state.ctxTokens = 285;
    state.autostart = true;
    state.translation = {
      text: 'Язык, которым мы пользуемся, формирует образ нашего мышления, а образ мышления формирует мир, который мы строим вместе. Маленькие словарные решения могут открывать двери — или тихо закрывать их.',
      at: now - 60000,
      stats: '0,7 с · токенов 53→41 · $0.000024'
    };
    state.chat = [
      { role: 'user', text: 'Can you explain the idea of linguistic relativity in simple terms?', at: now - 45000 },
      { role: 'bot', text: 'Лингвистическая относительность — это идея о том, что язык, на котором мы говорим, влияет на то, как мы видим мир и как думаем. Не определяет полностью, но мягко направляет.', at: now - 30000 }
    ];
    render();
    setStatus('Предпросмотр интерфейса: данные демонстрационные', 'warn');
  }

  if (inApp) send({ cmd: 'ready' });
  else if (demo) { document.title = 'Подстрочник — предпросмотр'; demoState(); }
  else { render(); }
})();
