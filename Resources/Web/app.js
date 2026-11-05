// Sync Pilot – Handy-Oberfläche
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
'use strict';

(function () {
  var $ = function (id) { return document.getElementById(id); };
  var state = { version: '', jobs: [], analysisJob: null, anVersion: -1, filter: 0, timer: null, items: [], gen: 0 };

  // ---------- Hilfen ----------

  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined && text !== null) e.textContent = text;
    return e;
  }

  var ICONS = {
    pc: '<rect x="3" y="4" width="18" height="12" rx="1.5"/><path d="M8 20h8M12 16v4"/>',
    laptop: '<rect x="5" y="5" width="14" height="10" rx="1.5"/><path d="M3 19h18"/>',
    server: '<rect x="5" y="3" width="14" height="7" rx="1.5"/><rect x="5" y="14" width="14" height="7" rx="1.5"/><path d="M9 6.5h.01M9 17.5h.01"/>',
    nas: '<rect x="4" y="4" width="16" height="16" rx="2"/><path d="M8 9h8M8 13h8M8 17h3"/>',
    usb: '<rect x="8" y="9" width="8" height="12" rx="1.5"/><path d="M10 9V4h4v5M11 6h.01M13 6h.01"/>',
    festplatte: '<rect x="3" y="7" width="18" height="10" rx="2"/><path d="M16 12h.01M7 12h5"/>',
    web: '<circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3a14 14 0 0 1 0 18M12 3a14 14 0 0 0 0 18"/>',
    cloud: '<path d="M7 18h10a4 4 0 0 0 .5-8A6 6 0 0 0 6 9.5 4.3 4.3 0 0 0 7 18z"/>',
    smartphone: '<rect x="7" y="3" width="10" height="18" rx="2"/><path d="M11 18h2"/>',
    ordner: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>'
  };

  function icon(name) {
    var span = el('span', 'dev');
    var path = Object.prototype.hasOwnProperty.call(ICONS, name) ? ICONS[name] : ICONS.ordner;
    span.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true">' + path + '</svg>';
    return span;
  }

  function api(method, path, body) {
    var opt = { method: method, credentials: 'same-origin', headers: {} };
    if (method !== 'GET') {
      opt.headers['X-Sync-Pilot'] = '1';
      opt.headers['Content-Type'] = 'application/json';
      opt.body = JSON.stringify(body || {});
    }
    return fetch(path, opt).then(function (r) {
      return r.json().catch(function () { return {}; }).then(function (data) {
        if (r.status === 401 && path !== '/api/login' && path !== '/api/pair') { showLogin(); throw new Error('login'); }
        data._status = r.status;
        return data;
      });
    });
  }

  // Jede Navigation erhöht gen; Antworten älterer Abrufe werden verworfen (keine doppelten Abfrageschleifen)
  function stopTimer() { state.gen++; if (state.timer) { clearTimeout(state.timer); state.timer = null; } }
  function later(fn, ms, g) { if (g === state.gen) state.timer = setTimeout(fn, ms); }

  // ---------- Ansichten ----------

  function show(view) {
    ['login', 'jobs', 'analysis', 'history', 'log'].forEach(function (v) { $('view-' + v).hidden = v !== view; });
    $('tabs').hidden = view === 'login';
    $('logout').hidden = view === 'login';
    var tab = view === 'analysis' ? 'jobs' : view === 'log' ? 'history' : view;
    Array.prototype.forEach.call(document.querySelectorAll('#tabs button'), function (b) {
      b.classList.toggle('active', b.getAttribute('data-go') === tab);
    });
    window.scrollTo(0, 0);
  }

  function route() {
    stopTimer();
    var h = location.hash.replace(/^#/, '');
    if (h.indexOf('pair=') === 0) { pair(decodeURIComponent(h.substring(5))); return; }
    if (h.indexOf('a/') === 0) openAnalysis(decodeURIComponent(h.substring(2)));
    else if (h.indexOf('log/') === 0) openLog(decodeURIComponent(h.substring(4)));
    else if (h === 'history') loadHistory();
    else loadJobs();
  }

  function go(hash) {
    if (location.hash === '#' + hash) route();
    else location.hash = hash;
  }

  // ---------- Anmeldung ----------

  function showLogin() {
    stopTimer();
    show('login');
    $('pw').value = '';
    setTimeout(function () { $('pw').focus(); }, 50);
  }

  $('login-form').addEventListener('submit', function (e) {
    e.preventDefault();
    $('login-error').textContent = '';
    api('POST', '/api/login', { password: $('pw').value }).then(function (d) {
      if (d._status !== 200) { $('login-error').textContent = d.error || 'Anmeldung fehlgeschlagen.'; return; }
      route();
    }).catch(function () { $('login-error').textContent = 'Keine Verbindung zu Sync Pilot.'; });
  });

  // Gekoppelt über den QR-Code aus Sync Pilot: Einmal-Code gegen Anmeldung tauschen, Code aus der Adresse entfernen
  function pair(code) {
    history.replaceState(null, '', location.pathname);
    api('POST', '/api/pair', { code: code }).then(function (d) {
      if (d._status !== 200) {
        showLogin();
        $('login-error').textContent = d.error || 'Koppeln fehlgeschlagen.';
        return;
      }
      route();
    }).catch(function () { showLogin(); $('login-error').textContent = 'Keine Verbindung zu Sync Pilot.'; });
  }

  $('logout').addEventListener('click', function () {
    api('POST', '/api/logout').then(showLogin, showLogin);
  });

  // ---------- Aufgaben ----------

  function loadJobs() {
    var g = state.gen;
    api('GET', '/api/state').then(function (d) {
      if (g !== state.gen) return;
      state.version = d.version;
      $('version').textContent = 'Version ' + d.version;
      $('info-version').textContent = 'Version ' + d.version;
      if (d.pc) $('info-pc').textContent = d.pc;
      $('copyright').textContent = '© ' + (d.year || new Date().getFullYear()) + ' Günther Schoder';
      state.jobs = d.jobs || [];
      renderJobs();
      show('jobs');
      later(loadJobs, 5000, g);
    }).catch(function (e) { if (e.message !== 'login') later(loadJobs, 5000, g); });
  }

  function renderJobs() {
    var box = $('jobs');
    box.innerHTML = '';
    if (!state.jobs.length) {
      box.appendChild(el('div', 'card empty muted', 'Noch keine Aufgaben. Aufgaben werden in Sync Pilot am PC angelegt.'));
      return;
    }
    state.jobs.forEach(function (j) {
      var card = el('div', 'card job');
      var dev = icon(j.devB === 'web' ? 'web' : j.devA);
      if (j.kind === 'err' || j.kind === 'warn') dev.classList.add(j.kind);
      card.appendChild(dev);
      var body = el('div');
      var name = el('div', 'name');
      name.appendChild(el('span', null, j.name));
      name.appendChild(el('span', 'chip ' + (j.busy ? 'none' : j.kind), j.busy ? (j.phase === 'sync' ? 'Synchronisiert …' : 'Analysiert …') : j.status));
      body.appendChild(name);
      body.appendChild(el('div', 'paths', j.a + (j.mode === 'Abgleichen' ? '  ⇄  ' : '  →  ') + j.b));
      body.appendChild(el('div', 'meta', 'Zuletzt: ' + j.last + ' · ' + j.modeText + ' · ' + j.schedule));
      card.appendChild(body);
      var buttons = el('div', 'buttons');
      var b1 = el('button', 'btn', j.busy ? 'Fortschritt ansehen' : 'Analysieren');
      b1.type = 'button';
      b1.addEventListener('click', j.busy ? function () { go('a/' + j.id); } : function () { start(j.id, false); });
      var b2 = el('button', 'btn soft', 'Synchronisieren');
      b2.type = 'button';
      b2.addEventListener('click', function () { start(j.id, true); });
      if (j.busy) b2.disabled = true;
      buttons.appendChild(b1);
      buttons.appendChild(b2);
      card.appendChild(buttons);
      box.appendChild(card);
    });
  }

  function start(id, sync) {
    stopTimer();
    api('POST', '/api/jobs/' + encodeURIComponent(id) + '/analyze', { sync: sync }).then(function () { go('a/' + id); }).catch(function () {});
  }

  // ---------- Analyse ----------

  var FILTERS = [
    { label: 'Alle', test: function () { return true; } },
    { label: '→ Nach B', test: function (i) { return i.chosen === 'NachB'; } },
    { label: '← Nach A', test: function (i) { return i.chosen === 'NachA'; } },
    { label: 'Löschen', test: function (i) { return i.chosen === 'LoeschenA' || i.chosen === 'LoeschenB'; } },
    { label: 'Konflikte & Übersprungen', test: function (i) { return i.chosen === 'Konflikt' || i.chosen === 'Keine'; } }
  ];

  function openAnalysis(id) {
    if (state.analysisJob !== id) {
      state.analysisJob = id; state.anVersion = -1; state.filter = 0; state.items = [];
      ['an-name', 'an-a', 'an-b', 'an-sa', 'an-sb', 'an-status', 'an-sum1', 'an-sum2', 'an-more'].forEach(function (x) { $(x).textContent = ''; });
      $('an-items').innerHTML = '';
      $('an-filter').innerHTML = '';
    }
    show('analysis');
    pollAnalysis();
  }

  function pollAnalysis() {
    var id = state.analysisJob, g = state.gen;
    api('GET', '/api/jobs/' + encodeURIComponent(id) + '/analysis').then(function (d) {
      if (g !== state.gen) return;
      if (d._status !== 200) { $('an-status').textContent = d.error || 'Fehler'; return; }
      renderAnalysis(d);
      var busy = d.phase === 'analyse' || d.phase === 'sync';
      later(pollAnalysis, busy ? 800 : 4000, g);
    }).catch(function (e) { if (e.message !== 'login') later(pollAnalysis, 3000, g); });
  }

  function renderAnalysis(d) {
    var busy = d.phase === 'analyse' || d.phase === 'sync';
    $('an-name').textContent = d.name;
    var status = d.error ? d.error : d.status;
    if (d.result && !busy) status = d.result + (status ? ' · ' + status : '');
    $('an-status').textContent = status || (d.phase === 'leer' ? 'Noch nicht analysiert.' : '');
    $('an-status').style.color = d.error || (d.result && d.resultBad) ? 'var(--err)' : '';
    $('an-a').textContent = d.a;
    $('an-b').textContent = d.b;
    $('an-mode').textContent = d.modeText;
    $('an-sa').textContent = d.filesA !== undefined ? d.filesA.toLocaleString('de-DE') + ' Dateien · ' + d.sizeA : '';
    $('an-sb').textContent = d.filesB !== undefined ? d.filesB.toLocaleString('de-DE') + ' Dateien · ' + d.sizeB : '';

    var prog = $('an-progress');
    prog.hidden = !busy;
    prog.classList.toggle('indet', !(d.phase === 'sync' && d.progN > 0));
    $('an-bar').style.width = d.phase === 'sync' && d.progN > 0 ? Math.round(d.progI * 100 / d.progN) + '%' : '';

    $('an-sync').hidden = busy;
    $('an-reanalyze').hidden = busy;
    $('an-cancel').hidden = !busy;
    $('an-sync').disabled = !d.items || !d.active;

    if (d.items && d.version !== state.anVersion) {
      state.anVersion = d.version;
      state.items = d.items;
      renderFilter();
      renderItems();
      $('an-more').textContent = d.more > 0 ? '… und ' + d.more + ' weitere Einträge (nur am PC sichtbar).' : '';
    } else if (!d.items) {
      state.items = [];
      $('an-filter').innerHTML = '';
      $('an-items').innerHTML = '';
      $('an-more').textContent = '';
    }

    if (busy) {
      $('an-sum1').textContent = d.phase === 'sync' && d.progN > 0 ? d.progI + ' von ' + d.progN + ' Aktionen' : 'Bitte warten …';
      $('an-sum2').textContent = '';
    } else if (d.items) {
      $('an-sum1').textContent = d.active ? d.active + (d.active === 1 ? ' Aktion' : ' Aktionen') + ' · ' + d.transfer + ' werden übertragen'
                                          : (d.items.length ? 'Keine Aktionen ausgewählt' : 'Alles aktuell – nichts zu tun');
      var hints = [];
      if (d.deletes) hints.push(d.deletes + ' Löschungen');
      if (d.conflicts) hints.push(d.conflicts + ' Konflikte werden übersprungen – Eintrag antippen zum Entscheiden');
      if (d.warn) hints.push('Achtung: ' + d.warn);
      $('an-sum2').textContent = hints.join(' · ');
    } else {
      $('an-sum1').textContent = '';
      $('an-sum2').textContent = '';
    }
  }

  function renderFilter() {
    var box = $('an-filter');
    box.innerHTML = '';
    FILTERS.forEach(function (f, n) {
      var b = el('button');
      b.type = 'button';
      b.setAttribute('aria-pressed', n === state.filter ? 'true' : 'false');
      b.appendChild(document.createTextNode(f.label));
      b.appendChild(el('span', 'n', String(state.items.filter(f.test).length)));
      b.addEventListener('click', function () { state.filter = n; renderFilter(); renderItems(); });
      box.appendChild(b);
    });
  }

  function renderItems() {
    var box = $('an-items');
    box.innerHTML = '';
    var list = state.items.filter(FILTERS[state.filter].test);
    if (!list.length) { box.appendChild(el('div', 'card empty muted', 'Keine Einträge.')); return; }
    list.slice(0, 1500).forEach(function (it) {
      var b = el('button', 'item' + (it.chosen !== it.proposed ? ' changed' : ''));
      b.type = 'button';
      b.appendChild(el('span', 'chip ' + it.chosen, it.label));
      b.appendChild(el('span', 'rel', it.rel));
      b.appendChild(el('span', 'sub', (it.chosen !== it.proposed ? 'Von dir geändert · ' : '') + it.reason + ' · ' + it.size + ' · A: ' + it.ta + ' · B: ' + it.tb));
      b.addEventListener('click', function () { chooseSheet(it); });
      box.appendChild(b);
    });
  }

  function chooseSheet(it) {
    var opts = [];
    if (it.hasA) opts.push(['NachB', '→ Nach B kopieren']);
    if (it.hasB) opts.push(['NachA', '← Nach A kopieren']);
    if (it.hasA) opts.push(['LoeschenA', 'In A löschen']);
    if (it.hasB) opts.push(['LoeschenB', 'In B löschen']);
    opts.push(['Keine', 'Überspringen']);
    if (it.chosen !== it.proposed) opts.push(['Vorschlag', 'Vorschlag wiederherstellen']);
    sheet(it.rel, it.reason, opts.map(function (o) {
      return [o[1], function () {
        api('POST', '/api/jobs/' + encodeURIComponent(state.analysisJob) + '/choose', { items: [it.i], rels: [it.rel], action: o[0] }).then(function (d) {
          if (d._status === 200) { state.anVersion = -1; renderAnalysis(d); }
          else { $('an-status').textContent = d.error || 'Fehler'; state.anVersion = -1; stopTimer(); pollAnalysis(); }
        }).catch(function () {});
      }, o[0] === it.chosen];
    }));
  }

  $('an-sync').addEventListener('click', function () { sync(false); });
  $('an-reanalyze').addEventListener('click', function () {
    api('POST', '/api/jobs/' + encodeURIComponent(state.analysisJob) + '/analyze', { sync: false }).then(function () { stopTimer(); pollAnalysis(); }).catch(function () {});
  });
  $('an-cancel').addEventListener('click', function () {
    api('POST', '/api/jobs/' + encodeURIComponent(state.analysisJob) + '/cancel').catch(function () {});
  });

  function sync(force) {
    api('POST', '/api/jobs/' + encodeURIComponent(state.analysisJob) + '/sync', { force: force }).then(function (d) {
      if (d._status === 409 && d.confirm) {
        if (window.confirm(d.confirm)) sync(true);
        return;
      }
      if (d._status !== 200) { $('an-status').textContent = d.error || 'Fehler'; return; }
      stopTimer();
      pollAnalysis();
    }).catch(function () {});
  }

  // ---------- Verlauf ----------

  function loadHistory() {
    var g = state.gen;
    api('GET', '/api/history').then(function (d) {
      if (g !== state.gen) return;
      var box = $('history');
      box.innerHTML = '';
      var runs = d.runs || [];
      if (!runs.length) box.appendChild(el('div', 'card empty muted', 'Noch keine Läufe.'));
      runs.forEach(function (r) {
        var b = el('button', 'run');
        b.type = 'button';
        b.appendChild(el('span', 'strong', r.job));
        b.appendChild(el('span', 'chip ' + r.kind, r.result.length > 34 ? r.result.substring(0, 33) + '…' : r.result));
        b.appendChild(el('span', 'sub', r.when + ' · ' + (r.scheduled ? 'Zeitplan' : 'Manuell') + ' · ' + r.duration));
        b.appendChild(el('span', 'sub', r.copied + ' kopiert · ' + r.deleted + ' gelöscht' + (r.errors ? ' · ' + r.errors + ' Fehler' : '')));
        if (r.hasLog) b.addEventListener('click', function () { go('log/' + r.key); });
        else b.disabled = true;
        box.appendChild(b);
      });
      show('history');
      later(loadHistory, 15000, g);
    }).catch(function (e) { if (e.message !== 'login') later(loadHistory, 15000, g); });
  }

  function openLog(key) {
    var g = state.gen;
    api('GET', '/api/log/' + encodeURIComponent(key)).then(function (d) {
      if (g !== state.gen) return;
      $('log-title').textContent = d._status === 200 ? d.job + ' · ' + d.when : 'Protokoll';
      $('log-text').textContent = d._status === 200 ? d.text : (d.error || 'Nicht gefunden.');
      show('log');
    }).catch(function () {});
  }

  // ---------- Auswahlblatt ----------

  function sheet(title, sub, buttons) {
    $('sheet-title').textContent = title;
    $('sheet-sub').textContent = sub || '';
    var box = $('sheet-buttons');
    box.innerHTML = '';
    buttons.forEach(function (b) {
      var btn = el('button', 'btn' + (b[2] ? ' soft' : ''), b[0]);
      btn.type = 'button';
      btn.addEventListener('click', function () { closeSheet(); if (b[1]) b[1](); });
      box.appendChild(btn);
    });
    $('sheet').hidden = false;
    var first = box.querySelector('button') || $('sheet-close');
    first.focus();
  }

  function closeSheet() { $('sheet').hidden = true; }
  $('sheet-close').addEventListener('click', closeSheet);
  $('sheet').addEventListener('click', function (e) { if (e.target === $('sheet')) closeSheet(); });
  document.addEventListener('keydown', function (e) { if (e.key === 'Escape') closeSheet(); });

  // Info wie in Wake on LAN: Logo, Version, Entwickler, Website, verbundener PC, Copyright
  function info(on) { $('info').hidden = !on; }
  $('version').addEventListener('click', function () { info(true); });
  $('info-close').addEventListener('click', function () { info(false); });
  $('info').addEventListener('click', function (e) { if (e.target === $('info')) info(false); });
  document.addEventListener('keydown', function (e) { if (e.key === 'Escape') info(false); });

  // ---------- Start ----------

  Array.prototype.forEach.call(document.querySelectorAll('[data-go]'), function (b) {
    b.addEventListener('click', function () { go(b.getAttribute('data-go')); });
  });
  window.addEventListener('hashchange', route);
  document.addEventListener('visibilitychange', function () { if (!document.hidden) route(); else stopTimer(); });
  route();
})();