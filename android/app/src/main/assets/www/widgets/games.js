import { API, D, PANEL_HEADERS, api, h, haptic, pad, t, toast } from '../main.js';
import { W } from '../registry.js';
import { every } from './helpers.js';

// ---------------------------------------------------------------- games
// Apex Legends ranked (Games): the agent reads TAS.gg's public overlay data for the channel in Settings › General ›
// Apex Legends and keeps the ranked session on the PC (/api/apex). The three widgets share one request.

let apexData = null, apexAt = 0, apexInflight = null;
function loadApex(maxAgeMs = 5000) {
  if (apexData && Date.now() - apexAt < maxAgeMs) return Promise.resolve(apexData);
  apexInflight ??= api('/apex').then((d) => { apexData = d; apexAt = Date.now(); return d; })
    .finally(() => { apexInflight = null; });
  return apexInflight;
}

const num = (v) => (v == null ? '—' : String(Math.abs(v)).replace(/\B(?=(\d{3})+(?!\d))/g, ' '));
const signed = (v) => (v == null ? '—' : (v > 0 ? '+' : v < 0 ? '−' : '') + num(v));
const tone = (v) => (v > 0 ? ' up' : v < 0 ? ' down' : ' flat');
const hm = (ms) => { const d = new Date(ms); return pad(d.getHours()) + ':' + pad(d.getMinutes()); };
// "Platinum IV" -> the tier in the panel's language, the division as it is
const rankName = (s) => {
  if (!s?.rank) return '—';
  const tr = s.tier ? t('apex.tier.' + s.tier) : '';
  if (!tr || tr.startsWith('apex.')) return s.rank;
  const division = s.rank.match(/\s([IVX]+)$/);
  return division ? tr + ' ' + division[1] : tr;
};
const img = (cls, src) => { const e = h('img', cls); e.alt = ''; e.decoding = 'async'; e.referrerPolicy = 'no-referrer'; if (src) e.src = src; else e.style.display = 'none'; e.onerror = () => { e.style.display = 'none'; }; return e; };

/** The empty states every Apex widget shares; null when there is data to show. */
function apexEmpty(d) {
  if (!d) return h('div', 'empty', t('w.loading'));
  if (!d.configured) return h('div', 'empty', t('apex.setup'));
  const s = d.snapshot;
  if (!s.updatedAt) return h('div', 'empty', s.error || t('w.loading'));
  return null;
}

function apexWidget(render) {
  return {
    needs: ['tick'],
    create(box, { o }) {
      let lastKey = '';
      const draw = (force) => {
        const key = JSON.stringify([apexData, box.clientHeight, box.clientWidth]);
        if (!force && key === lastKey) return;
        lastKey = key;
        const empty = apexEmpty(apexData);
        if (empty) { box.replaceChildren(empty); return; }
        render(box, apexData.snapshot, o, () => draw(true));
      };
      const tick = every(15_000, async () => { try { await loadApex(); } catch (_) { return false; } draw(); });
      return { update: () => { tick(); draw(); } };
    },
  };
}

W['apex-rank'] = apexWidget((box, s, o) => {
  const ss = s.session, games = ss.games || [], last = games[games.length - 1];
  const kids = [
    h('div', 'ax-head',
      h('div', 'ax-badge', img('ax-badge-img', s.rankIcon),
        last ? h('span', 'ax-chip' + tone(last.rp), signed(last.rp)) : null),
      h('div', 'ax-name', h('span', 'ax-k', t('apex.rank')), h('b', null, rankName(s)))),
  ];
  if (o.next !== false && (s.next || s.predatorPosition)) {
    const pct = s.next ? Math.max(2, Math.min(100, s.next.percent)) : 100;
    const bar = h('div', 'ax-bar', h('i'));
    bar.firstChild.style.width = pct + '%';
    kids.push(h('div', 'ax-next',
      s.next ? img('ax-next-img', s.next.icon) : null,
      h('div', null, h('span', 'ax-k', s.next ? t('apex.toRank', { rank: rankName({ rank: s.next.name, tier: 'RANK_' + s.next.name.toUpperCase() }) }) : t('apex.predator')),
        h('b', null, s.next ? num(s.next.remaining) + ' RP' : '#' + s.predatorPosition))), bar);
  }
  kids.push(h('div', 'ax-rp',
    h('div', null, h('span', 'ax-k', t('apex.rp')), h('b', null, num(s.rp))),
    h('div', null, h('span', 'ax-k', t('apex.session')), h('b', 'ax-delta' + tone(ss.rp ?? 0), ss.active ? signed(ss.rp) : '—'))));
  if (o.games !== false) {
    kids.push(h('div', 'ax-games',
      h('span', 'ax-k', t('apex.recent')),
      h('div', 'ax-glist', games.length
        ? games.slice(-6).reverse().map((g) => h('span', 'ax-g' + tone(g.rp), signed(g.rp),
          h('small', null, (g.kills != null ? g.kills + 'K · ' : '') + hm(g.at))))
        : h('span', 'ax-none', ss.active ? t('apex.noGames') : t('apex.waiting')))));
  }
  box.replaceChildren(...kids);
});

W['apex-session'] = apexWidget((box, s, o, redraw) => {
  const ss = s.session;
  const stat = (k, v) => h('div', 'ax-st', h('span', 'ax-k', k), h('b', null, v));
  const top = h('div', 'ax-sess-top', h('span', 'ax-k ax-since', ss.active ? t('apex.since', { time: hm(ss.startedAt) }) : t('apex.waiting')));
  const kids = [top,
    h('div', 'ax-stats', stat(t('apex.kills'), ss.active ? num(ss.kills) : '—'), stat(t('apex.wins'), ss.active ? num(ss.wins) : '—'),
      stat(t('apex.started'), ss.active ? num(ss.baseRp) : '—'))];
  if (o.button !== false) {
    const btn = h('button', 'ax-start', h('span', 'ax-start-ic', '▶'), h('span', null, t('apex.startSession')));
    btn.addEventListener('click', async () => {
      haptic();
      btn.classList.add('busy');
      try {
        const r = await fetch(`${API}/apex/session`, { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS });
        if (!r.ok) toast((await r.json().catch(() => ({}))).error || t('panel.pcNoAnswer'), 'err');
      } catch (_) { toast(t('panel.pcNoAnswer'), 'err'); }
      setTimeout(async () => { try { await loadApex(0); } catch (_) {} btn.classList.remove('busy'); redraw(); }, 2500);
    });
    top.append(btn);
  }
  box.replaceChildren(...kids);
});

W['apex-account'] = apexWidget((box, s) => {
  const cell = (k, v, pic) => h('div', 'ax-cell', h('span', 'ax-k', k), h('b', null, pic, v));
  box.replaceChildren(h('div', 'ax-acct',
    cell(t('apex.level'), (s.level ?? '—') + (s.prestige ? ' · ' + t('apex.prestige', { n: s.prestige.replace(/\D/g, '') }) : '')),
    cell(t('apex.brWins'), num(s.brWins)),
    cell(t('apex.brKills'), num(s.brKills)),
    cell(t('apex.legend'), s.legend || '—', s.legendIcon ? img('ax-legend-img', s.legendIcon) : null)));
});

// Go live: one button per OBS profile (each profile has its own service, stream key and quality). The agent switches
// profile and starts the stream (/api/obs/golive); while live the buttons turn into Stop.
W['obs-golive'] = {
  needs: ['tick'],
  create(box, { o }) {
    let data = null, lastKey = '', fetchedAt = 0, busy = null;
    const load = async () => { try { data = await api('/obs'); fetchedAt = Date.now(); } catch (_) { return false; } };
    const dur = (ms) => { const sec = Math.max(0, Math.floor(ms / 1000)); return pad(Math.floor(sec / 3600)) + ':' + pad(Math.floor(sec / 60) % 60) + ':' + pad(sec % 60); };
    const post = async (path) => {
      haptic();
      try {
        const r = await fetch(`${API}/obs/${path}`, { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS });
        if (!r.ok) toast((await r.json().catch(() => ({}))).error || 'OBS did not do it', 'err');
      } catch (_) { toast(t('panel.pcNoAnswer'), 'err'); }
      await load(); busy = null; render(true);
    };
    const brand = (name) => { const n = name.toLowerCase(); return n.includes('twitch') ? 'tw' : n.includes('youtube') ? 'yt' : n.includes('kick') ? 'kk' : 'ot'; };
    const render = (force = false) => {
      const s = data?.snapshot;
      const since = Date.now() - fetchedAt;
      const key = JSON.stringify([data, busy, Math.floor(Date.now() / 1000), box.clientHeight]);
      if (!force && key === lastKey) return;
      lastKey = key;
      if (!data) { box.replaceChildren(h('div', 'empty', t('w.loading'))); return; }
      if (!data.enabled) { box.replaceChildren(h('div', 'empty', t('golive.turnOn'))); return; }
      if (!s.connected) { box.replaceChildren(h('div', 'empty', s.error || t('panel.connecting'))); return; }
      const live = s.streaming;
      const wanted = (o.profiles || []).map((x) => String(x).trim()).filter(Boolean);
      const profiles = (wanted.length ? wanted : s.profiles || []).filter((p) => (s.profiles || []).includes(p));
      const ctl = data.control && o.control === true;
      const dropped = s.totalFrames ? (100 * s.skippedFrames / s.totalFrames).toFixed(1) : '0.0';
      const info = h('div', 'gl-info',
        h('span', 'gl-k', live ? t('golive.liveOn', { profile: s.profile || '' }) : t('golive.title')),
        h('span', 'gl-st', h('i', 'gl-dot'), live ? (s.reconnecting ? t('golive.reconnecting') : dur(s.streamMs + since)) : (busy ? t('golive.starting') : t('golive.offline'))),
        h('span', 'gl-sub', live ? t('golive.dropped', { n: s.skippedFrames, pct: dropped }) : t('golive.profile', { profile: s.profile || '—' })));
      const btns = h('div', 'gl-bts' + (busy ? ' busy' : ''));
      if (live) {
        const b = h('button', 'gl-b gl-stop', h('span', 'gl-ic', '■'), t('golive.stop'));
        if (ctl) b.addEventListener('click', () => { busy = 'stop'; render(true); post('stream'); });
        btns.append(b);
      } else {
        for (const p of profiles) {
          const b = h('button', 'gl-b gl-' + brand(p), h('span', 'gl-ic', '●'), p);
          if (ctl) b.addEventListener('click', () => { busy = p; render(true); post('golive?profile=' + encodeURIComponent(p)); });
          btns.append(b);
        }
        if (!profiles.length) btns.append(h('span', 'gl-sub', t('golive.noProfiles')));
      }
      if (!ctl) btns.title = t('golive.controlOff');
      box.classList.toggle('gl-live', !!live);
      box.replaceChildren(info, btns);
    };
    const tick = every(2000, async () => { await load(); render(); });
    return { update: () => { tick(); render(); } };
  },
};

// Fans (Computer): every fan LibreHardwareMonitor sees, with its speed and the duty its controller asks for. A pushed
// value "fans" (a fan controller script, for example) adds its mode on top. Names can be renamed in the options
// ("CPU Fan=Radiator").
W.fans = {
  needs: ['state', 'push'],
  create(box, { o }) {
    let lastKey = '';
    const renames = {};
    for (const line of o.names || []) { const i = String(line).indexOf('='); if (i > 0) renames[line.slice(0, i).trim()] = line.slice(i + 1).trim(); }
    return {
      update() {
        const fans = D.state?.pc?.fans;
        const mode = D.push?.fans?.data;
        const key = JSON.stringify([fans, mode?.value, mode?.sub, box.clientHeight]);
        if (key === lastKey) return;
        lastKey = key;
        const head = h('div', 'fan-head', h('span', 'fan-title', t('fans.title')),
          mode?.value ? h('span', 'fan-mode fan-' + (mode.level || 'ok'), String(mode.value)) : null);
        if (!fans) { box.replaceChildren(head, h('div', 'empty', t('fans.needsLhm'))); return; }
        if (!fans.length) { box.replaceChildren(head, h('div', 'empty', t('fans.none'))); return; }
        const rows = fans.map((f) => {
          const pct = f.percent != null ? f.percent : null;
          const bar = h('div', 'track', h('i'));
          bar.firstChild.style.width = Math.max(0, Math.min(100, pct ?? 0)) + '%';
          const stopped = f.rpm <= 0;
          return h('div', 'fan-row' + (stopped ? ' stopped' : ''),
            h('div', 'm-row', h('span', null, renames[f.name] || f.name),
              h('span', null, stopped ? t('fans.stopped') : `${Math.round(f.rpm)} RPM` + (pct != null ? ` · ${Math.round(pct)}%` : ''))),
            bar);
        });
        box.replaceChildren(head, h('div', 'fan-list', rows), mode?.sub ? h('div', 'fan-sub', String(mode.sub)) : null);
      },
    };
  },
};
