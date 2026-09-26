import json, math, os

# Builds the mockup pages in this folder: python3 gen.py (style.css is edited by hand).
OUT = os.path.dirname(os.path.abspath(__file__))
NEWS = os.path.join(OUT, '..', '..', 'src', 'DarkHaven.Launcher', 'Assets', 'news.json')

W, H = 960, 600
RAIL = 144
MAIN_H = H - 32

NAV = [('home.html', 'Главная'), ('regions.html', 'Регионы'), ('servers.html', 'Серверы'),
       ('monitoring.html', 'Мониторинг'), ('news.html', 'Новости'), ('settings.html', 'Настройки')]

I = lambda cp: f'<span class="icon">&#x{cp};</span>'
PLAY, CHEV, BELL, ACC = I('EB2C'), I('EAB6'), I('EAA2'), I('EB99')
ST_TITLE = {'on': 'в сети', 'q': 'карантин', 'full': 'полный', 'off': 'офлайн'}


def st(kind, title=None):
    return f'<i class="st {kind}" title="{title or ST_TITLE[kind]}"></i>'


def count(n, cap, cls=''):
    return f'<span class="mono num {cls}">{n}/{cap}</span>'


BASE_CSS = '''
.num{color:var(--text)}
'''


def page(file, title, active, main, css='', aside='', overlay='', account_on=False, main_cls='main', update=False):
    nav = '\n'.join(f'<a href="{h}"{" class=\"on\"" if h == active else ""}>{t}</a>' for h, t in NAV)
    upd = '<button class="upd">0.2.5 · обновить</button>' if update else ''
    html = f'''<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<title>{title}</title>
<link rel="stylesheet" href="style.css">
<style>
{BASE_CSS.strip()}
{css.strip()}
</style>
</head>
<body>
<div class="win">

<header class="top">
<div class="mark">FRONTIER 15</div>
<div class="top-r">
<button class="top-btn" aria-label="Уведомления">{BELL}</button>
<a class="top-btn{' on' if account_on else ''}" href="account.html">{ACC}Гость</a>
<div class="wc">
<button class="min" aria-label="Свернуть"></button>
<button class="cls" aria-label="Закрыть"></button>
</div>
</div>
</header>

<div class="body">
<nav class="rail">
<div class="nav">
{nav}
</div>
<div class="ver">
<div><span>ЛАУНЧЕР</span>0.2.4</div>
<div><span>ДВИЖОК</span>275.1.0</div>
{upd}
</div>
</nav>

<main class="{main_cls}">
{main.strip()}
</main>
{aside.strip()}
</div>
{overlay.strip()}
</div>
</body>
</html>
'''
    with open(os.path.join(OUT, file), 'w') as f:
        f.write(html)


# ---------------------------------------------------------------- home
REGIONS = [('ХЕЙВЕН', 'on', (42, 80)), ('РУБЕЖ', 'q', None), ('ВЕРФЬ', 'q', None), ('КАРАНТИН', 'q', None), ('ПОЛИГОН', 'q', None)]

HOME_CSS = '''
.hero{height:52px;flex-shrink:0;display:flex;align-items:stretch;border:1px solid var(--line)}
.hero-name{flex-grow:1;padding:0 16px;display:flex;flex-direction:column;justify-content:center;gap:4px;min-width:0}
.stat{width:88px;padding:0 12px;border-left:1px solid var(--line);display:flex;flex-direction:column;justify-content:center;gap:4px}
.stat b{font-family:'JBM',monospace;font-weight:400;font-size:12px}
.stat > span:not(.num){font-size:10.5px;color:var(--dim)}
.stat .num{font-size:12px}
.hero .play{width:128px}
.regions{display:grid;grid-template-columns:repeat(5,1fr);border:1px solid var(--line);margin-top:-8px}
.reg{height:28px;padding:0 12px;border-left:1px solid var(--line);display:flex;align-items:center;gap:8px;color:var(--dim);text-align:left}
.reg:first-child{border-left:0}
.reg:hover{background:var(--hover)}
.reg.on{color:var(--text);outline:1px solid var(--text);outline-offset:-1px;background:var(--panel)}
.reg b{flex-grow:1;font-size:11.5px;font-weight:500}
.reg .mono{font-size:10px}
.lists{display:grid;grid-template-columns:1fr 1fr;gap:20px}
.friends{width:200px;flex-shrink:0;border-left:1px solid var(--line);display:flex;flex-direction:column}
.friends .cap{height:28px;padding:0 12px}
.fr{height:28px;padding:0 4px 0 12px;display:flex;align-items:center;gap:8px;border-bottom:1px solid var(--line);font-size:12px}
.fr b{font-weight:400;white-space:nowrap}
.fr span{flex-grow:1;font-size:10.5px;color:var(--dim);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.fr.off,.fr.off span{color:var(--faint)}
.join{height:20px;padding:0 8px;border:1px solid var(--line-strong);font-size:10.5px}
.join:hover{border-color:var(--text)}
'''


def lrow(name, kind, pop=''):
    if kind == 'off':
        return f'<button class="row off">{st("off")}<span>{name}</span><span class="mono">—</span><span></span></button>'
    return f'<button class="row">{st(kind)}<span>{name}</span><span class="mono">{pop}</span>{CHEV}</button>'


def home_main():
    regs = []
    for n, k, c in REGIONS:
        num = f'<span class="mono">{c[0]}</span>' if c else ''
        regs.append(f'<button class="reg{" on" if k == "on" else ""}">{st(k)}<b>{n}</b>{num}</button>')
    return f'''
<div class="hero">
<div class="hero-name">
<h1 class="name">ХЕЙВЕН</h1>
<div class="addr mono">ss14://95.31.51.216:1212</div>
</div>
<div class="stat">{count(42, 80, "flash")}<span>игроков</span></div>
<div class="stat"><b>01:12</b><span>раунд</span></div>
<div class="stat"><b>38 мс</b><span>отклик</span></div>
<a class="play" href="connecting.html">{PLAY}ИГРАТЬ</a>
</div>

<div class="regions">
{chr(10).join(regs)}
</div>

<div class="lists">
<section>
<div class="cap"><span>ИЗБРАННОЕ</span><span>4</span></div>
{lrow("Станция Орион", "on", "31/60")}
{lrow("Nova RP", "full", "64/64")}
{lrow("Глубина", "on", "12/40")}
{lrow("Сектор 7", "off")}
</section>
<section>
<div class="cap"><span>НЕДАВНИЕ</span><span>5</span></div>
{lrow("ХЕЙВЕН", "on", "42/80")}
{lrow("Nova RP", "full", "64/64")}
{lrow("Станция Орион", "on", "31/60")}
{lrow("Глубина", "on", "12/40")}
{lrow("Сектор 7", "off")}
</section>
</div>
'''


HOME_ASIDE = f'''
<aside class="friends">
<div class="cap"><span>ДРУЗЬЯ</span><span>2/4</span></div>
<div class="fr">{st("on", "в игре")}<b>Kestrel</b><span>ХЕЙВЕН</span><button class="join">Зайти</button></div>
<div class="fr">{st("on", "в игре")}<b>Лада_7</b><span>Nova RP</span><button class="join">Зайти</button></div>
<div class="fr">{st("q", "в сети")}<b>Мирон</b><span></span></div>
<div class="fr off">{st("off", "не в сети")}<b>vex</b><span></span></div>
</aside>
'''

page('home.html', 'Главная', 'home.html', home_main(), HOME_CSS, HOME_ASIDE)
page('home-update.html', 'Главная — есть обновление', 'home.html', home_main(), HOME_CSS, HOME_ASIDE, update=True)

# ---------------------------------------------------------------- connecting (over home)
CONNECT_CSS = HOME_CSS + '''
.veil{align-items:center}
.dlg.conn{width:400px;padding:16px;display:flex;flex-direction:column;gap:12px}
.dlg-head{display:flex;flex-direction:column;gap:8px}
.step{height:24px;display:grid;grid-template-columns:8px 1fr auto;gap:12px;align-items:center;border-bottom:1px solid var(--line);font-size:12px}
.step .mono{font-size:10.5px;color:var(--dim)}
.step.wait{color:var(--faint)}
.bar{height:1px;background:var(--line-strong)}
.bar i{display:block;height:100%;width:26%;background:var(--text)}
.dlg-foot{display:flex;justify-content:space-between;align-items:center}
'''
CONNECT_OVERLAY = f'''
<div class="veil">
<div class="dlg conn" role="dialog" aria-label="Подключение">
<div class="dlg-head">
<div class="lbl">ПЕРЕХОД В РЕГИОН</div>
<div class="name">ХЕЙВЕН</div>
</div>
<div>
<div class="step">{st("on", "готово")}<span>Версия движка</span><span class="mono">275.1.0</span></div>
<div class="step">{st("full", "идёт")}<span>Загрузка контента</span><span class="mono">4 132 / 15 901</span></div>
<div class="step wait">{st("q", "ждёт")}<span>Проверка файлов</span><span></span></div>
<div class="step wait">{st("q", "ждёт")}<span>Запуск игры</span><span></span></div>
</div>
<div class="bar"><i></i></div>
<div class="dlg-foot">
<span class="mono dim" style="font-size:10.5px">4,2 МБ/с · ~1 мин</span>
<a class="btn" href="home.html">Отмена</a>
</div>
</div>
</div>
'''
page('connecting.html', 'Подключение', 'home.html', home_main(), CONNECT_CSS, HOME_ASIDE, CONNECT_OVERLAY)

# ---------------------------------------------------------------- quick switcher (Ctrl+K) over home
PALETTE_CSS = HOME_CSS + '''
.veil{align-items:flex-start;padding-top:64px}
.dlg.pal{width:480px}
.pal-in{height:36px;display:flex;align-items:center;gap:8px;padding:0 12px;border-bottom:1px solid var(--line)}
.pal-in .icon{font-size:12px;color:var(--dim)}
.pal-in input{flex-grow:1;background:none;border:0;outline:0;color:var(--text);font-family:'Inter',sans-serif;font-size:13px}
.hit{height:28px;width:100%;display:grid;grid-template-columns:8px 1fr auto;gap:12px;align-items:center;padding:0 12px;font-size:12px;text-align:left}
.hit:hover,.hit.sel{background:var(--hover)}
.hit .lbl{letter-spacing:.08em}
.hit u{text-decoration:none;color:var(--text);border-bottom:1px solid var(--dim)}
.hits{padding:4px 0}
'''
PALETTE_OVERLAY = f'''
<div class="veil">
<div class="dlg pal" role="dialog" aria-label="Быстрый переход">
<div class="pal-in">{I("EA6D")}<input value="но" aria-label="Куда"></div>
<div class="hits">
<button class="hit sel">{st("full")}<span><u>No</u>va RP</span><span class="lbl">СЕРВЕР</span></button>
<button class="hit">{st("on")}<span><u>No</u>va Classic</span><span class="lbl">СЕРВЕР</span></button>
<button class="hit">{st("full")}<span><u>No</u>va Frontier</span><span class="lbl">СЕРВЕР</span></button>
<button class="hit"><span></span><span><u>Но</u>вости</span><span class="lbl">РАЗДЕЛ</span></button>
</div>
</div>
</div>
'''
page('palette.html', 'Быстрый переход', 'home.html', home_main(), PALETTE_CSS, HOME_ASIDE, PALETTE_OVERLAY)

# ---------------------------------------------------------------- regions
DETAIL_W = 272
MAP_W, MAP_H = W - RAIL - 1 - DETAIL_W - 1, MAIN_H
NODES = [('ХЕЙВЕН', .5, .5, True), ('РУБЕЖ', .22, .3, False), ('ВЕРФЬ', .78, .28, False),
         ('КАРАНТИН', .8, .72, False), ('ПОЛИГОН', .28, .78, False)]


def pos(fx, fy):
    return round(56 + fx * (MAP_W - 136)), round(48 + fy * (MAP_H - 96))


def regions_page():
    cx, cy = pos(.5, .5)
    grid = ''.join(f'<line x1="{x}" y1="0" x2="{x}" y2="{MAP_H}"></line>' for x in range(16, MAP_W, 48)) + \
           ''.join(f'<line x1="0" y1="{y}" x2="{MAP_W}" y2="{y}"></line>' for y in range(8, MAP_H, 48))
    links = ''.join(f'<line x1="{cx}" y1="{cy}" x2="{pos(fx, fy)[0]}" y2="{pos(fx, fy)[1]}"></line>'
                    for n, fx, fy, on in NODES if not on)
    nodes = []
    for n, fx, fy, on in NODES:
        x, y = pos(fx, fy)
        if on:
            nodes.append(f'<button class="node on" style="left:{x - 10}px;top:{y - 10}px"><i class="sq"><i></i></i>'
                         f'<span><b>{n}</b>{count(42, 80)}</span></button>')
        else:
            nodes.append(f'<button class="node" style="left:{x - 4}px;top:{y - 4}px" title="карантин"><i class="sq"></i><span><b>{n}</b></span></button>')
    css = f'''
.main{{padding:0;flex-direction:row;gap:0}}
.map{{position:relative;flex-grow:1;overflow:hidden}}
.map svg{{position:absolute;left:0;top:0}}
.map .grid line{{stroke:#141416;stroke-width:1}}
.map .links line{{stroke:#3A3A3E;stroke-width:1;stroke-dasharray:4 4}}
.total{{position:absolute;right:16px;top:12px;font-family:'JBM',monospace;font-size:10px;color:var(--dim)}}
.node{{position:absolute;display:flex;align-items:flex-start;gap:8px;color:var(--dim);text-align:left}}
.node .sq{{width:8px;height:8px;border:1px solid #6E6E73;background:var(--bg);flex-shrink:0}}
.node span{{display:flex;flex-direction:column;gap:4px;margin-top:-4px}}
.node b{{font-size:12px;font-weight:500;letter-spacing:.04em}}
.node:hover{{color:var(--text)}}
.node.on{{color:var(--text)}}
.node.on .sq{{width:20px;height:20px;border-color:var(--text);display:flex;align-items:center;justify-content:center}}
.node.on .sq i{{width:8px;height:8px;background:var(--text)}}
.node.on span{{margin-top:0}}
.node.on b{{font-size:14px;font-weight:600}}
.node.on .num{{font-size:10px}}
.detail{{width:{DETAIL_W}px;flex-shrink:0;border-left:1px solid var(--line);padding:16px;display:flex;flex-direction:column;gap:16px}}
.detail .kvs{{border-top:1px solid var(--line)}}
.detail .acts{{display:flex;flex-direction:column;gap:8px}}
.detail .acts .play{{height:28px}}
.nb{{height:24px;width:100%;display:flex;align-items:center;gap:8px;border-bottom:1px solid var(--line);font-size:12px;color:var(--dim)}}
.nb:hover{{color:var(--text);background:var(--hover)}}
'''
    neigh = ''.join(f'<button class="nb">{st("q")}<span>{n}</span></button>' for n, *_ in NODES[1:])
    main = f'''
<div class="map">
<svg width="{MAP_W}" height="{MAP_H}" aria-hidden="true"><g class="grid">{grid}</g><g class="links">{links}</g></svg>
<div class="total">в сети 42</div>
{''.join(nodes)}
</div>
<div class="detail">
<div style="display:flex;flex-direction:column;gap:4px">
<h1 class="name">ХЕЙВЕН</h1>
<div class="addr mono">ss14://95.31.51.216:1212</div>
</div>
<div class="kvs">
<div class="kv"><span>Состояние</span><span style="display:flex;align-items:center;gap:8px">{st("on")}в сети</span></div>
<div class="kv"><span>Игроков</span>{count(42, 80)}</div>
<div class="kv"><span>Раунд</span><span class="mono">01:12</span></div>
<div class="kv"><span>Отклик</span><span class="mono">38 мс</span></div>
</div>
<div class="acts">
<a class="play" href="connecting.html">{PLAY}ИГРАТЬ</a>
<button class="btn wide">Баны игроков</button>
</div>
<div>
<div class="cap"><span>СОСЕДИ</span><span>4</span></div>
{neigh}
</div>
</div>
'''
    page('regions.html', 'Регионы', 'regions.html', main, css)


regions_page()

# ---------------------------------------------------------------- servers
GROUPS = [
    ('FRONTIER 15', [('ХЕЙВЕН', ['RP: средний'], 38, (42, 80), True)]),
    ('NOVA', [('Nova RP', ['RP: высокий', '18+'], 71, (64, 64), True),
              ('Nova Classic', ['RP: лёгкий'], 72, (19, 64), False),
              ('Nova Frontier', ['RP: средний'], 70, (64, 64), False)]),
    ('ORBITAL', [('Orbital One', ['RP: средний'], 64, (22, 48), False),
                 ('Orbital Two', ['без RP'], 66, (9, 48), False)]),
    ('ПРОЧИЕ', [('Станция Орион', ['RP: средний'], 54, (31, 60), True),
                ('Глубина', ['RP: высокий'], 88, (12, 40), True),
                ('Мостик', ['без RP'], 46, (7, 32), False),
                ('Эхо-9', ['RP: лёгкий', '18+'], 120, (3, 50), False),
                ('Причал', ['RP: средний'], 95, (18, 40), False),
                ('Сектор 7', ['RP: средний'], None, None, True)]),
]


def servers_page():
    css = '''
.toolbar{display:flex;gap:4px;align-items:center}
.toolbar .search{flex-grow:1}
.toolbar .direct{width:200px}
.toolbar .sep{margin:0 4px}
.filters{display:flex;align-items:center;gap:8px;margin-top:-8px}
.list{display:flex;flex-direction:column;gap:12px;min-height:0;flex-grow:1;margin-top:-16px;padding-right:8px}
.srv{height:24px;display:grid;grid-template-columns:16px 8px 1fr auto 44px 44px 52px;gap:12px;align-items:center;border-bottom:1px solid var(--line);font-size:12px}
.srv:hover{background:var(--hover)}
.srv .fav{font-family:'JBM',monospace;font-size:11px;color:var(--faint);text-align:center}
.srv .fav.on{color:var(--text)}
.srv .tags{display:flex;gap:4px}
.srv .mono{font-size:10.5px;text-align:right}
.srv .ping{color:var(--dim)}
.srv .go{height:20px;padding:0 8px;border:1px solid var(--line-strong);font-size:10.5px;display:inline-flex;align-items:center;justify-content:center}
.srv .go:hover{border-color:var(--text)}
.srv.off{color:var(--faint)}
.srv.full .num{color:var(--dim)}
'''
    groups = []
    for label, rows in GROUPS:
        players = sum(r[3][0] for r in rows if r[3])
        body = []
        for name, tags, ping, pop, fav in rows:
            off = pop is None
            full = not off and pop[0] == pop[1]
            kind = 'off' if off else 'full' if full else 'on'
            tg = ''.join(f'<span class="tag">{t}</span>' for t in tags)
            cls = 'srv' + (' off' if off else '') + (' full' if full else '')
            favg = f'<button class="fav{" on" if fav else ""}" aria-label="Избранное">&#x{"EB59" if fav else "EA6A"};</button>'
            go = '<span></span>' if off else '<a class="go" href="connecting.html">Играть</a>'
            popc = '<span class="mono">—</span>' if off else count(*pop)
            pingc = '—' if off else f'{ping} мс'
            body.append(f'<div class="{cls}">{favg}{st(kind)}<span>{name}</span><span class="tags">{tg}</span>'
                        f'<span class="mono ping">{pingc}</span>{popc}{go}</div>')
        groups.append(f'<section><div class="cap"><span>{label}</span><span>{len(rows)} · {players}</span></div>\n' + '\n'.join(body) + '</section>')
    main = f'''
<div class="toolbar">
<label class="field search">{I("EA6D")}<input placeholder="Поиск"></label>
<label class="field direct"><input class="mono" placeholder="ss14://"></label>
<button class="ibtn" aria-label="Подключиться">&#xEA9C;</button>
<span class="sep"></span>
<button class="ibtn" aria-label="Карта">&#xEC05;</button>
<button class="ibtn" aria-label="Обновить">&#xEB37;</button>
<button class="btn">{I("EA60")}Разместить</button>
</div>
<div class="filters">
<span class="lbl">СКРЫТЬ</span>
<button class="chip on">пустые</button>
<button class="chip">полные</button>
<button class="chip">18+</button>
<span class="sep"></span>
<button class="chip">{I("EB59")}избранное</button>
<button class="chip">А–Я</button>
<span class="sep"></span>
<span class="lbl">RP</span>
<span class="seg"><button class="chip">нет</button><button class="chip on">лёгкий</button><button class="chip on">средний</button><button class="chip on">высокий</button></span>
</div>
<div class="loading" aria-label="Обновляется"></div>
<div class="list scroll">
{chr(10).join(groups)}
</div>
'''
    page('servers.html', 'Серверы', 'servers.html', main, css)


servers_page()

# ---------------------------------------------------------------- monitoring
def chart(w, h, pts, cap=None, ylabels=(), xlabels=()):
    pad_l, pad_b, pad_t = 24, 16 if xlabels else 4, 4
    iw, ih = w - pad_l, h - pad_b - pad_t
    top = cap or max(pts)

    def P(i, v):
        return pad_l + i * iw / (len(pts) - 1), pad_t + ih - v / top * ih
    line = ' '.join(f'{x:.1f},{y:.1f}' for x, y in (P(i, v) for i, v in enumerate(pts)))
    area = f'{pad_l},{pad_t + ih} ' + line + f' {pad_l + iw},{pad_t + ih}'
    g = []
    for v in ylabels:
        y = pad_t + ih - v / top * ih
        g.append(f'<line x1="{pad_l}" y1="{y:.1f}" x2="{w}" y2="{y:.1f}" class="gl"></line>'
                 f'<text x="{pad_l - 6}" y="{y + 3:.1f}" text-anchor="end">{v}</text>')
    for i, t in xlabels:
        x = pad_l + i * iw / (len(pts) - 1)
        anchor = 'end' if i == len(pts) - 1 else 'start' if i == 0 else 'middle'
        g.append(f'<text x="{x:.1f}" y="{h - 3}" text-anchor="{anchor}">{t}</text>')
    capl = f'<line x1="{pad_l}" y1="{pad_t}" x2="{w}" y2="{pad_t}" class="cl"></line>' if cap else ''
    return (f'<svg width="{w}" height="{h}" class="chart" aria-hidden="true">{"".join(g)}{capl}'
            f'<polygon points="{area}" class="ar"></polygon><polyline points="{line}" class="ln"></polyline></svg>')


def monitoring_page():
    pts = [max(3, round(14 + 26 * (0.5 - 0.5 * math.cos((i - 6) / 48 * 2 * math.pi)) + 4 * math.sin(i * 1.7) + (10 if 34 < i < 44 else 0))) for i in range(49)]
    lp = [round(40 + 30 * (0.5 - 0.5 * math.cos((i - 6) / 48 * 2 * math.pi)) + 5 * math.sin(i * 2.3)) for i in range(49)]
    inner = W - RAIL - 1 - 40
    css = '''
.switch{display:flex;gap:8px;align-items:center}
.now{height:52px;flex-shrink:0;display:flex;align-items:stretch;border:1px solid var(--line);margin-top:-8px}
.now .cell{padding:0 12px;border-left:1px solid var(--line);display:flex;flex-direction:column;justify-content:center;gap:4px}
.now .cell:first-child{border-left:0;flex-grow:1}
.now .cell b{font-family:'JBM',monospace;font-weight:400;font-size:12px;display:flex;align-items:center;gap:8px}
.now .cell > span:not(.num){font-size:10.5px;color:var(--dim)}
.now .num{font-size:12px}
.now .play{width:128px}
.copy{font-family:'JBM',monospace;font-size:10.5px;color:var(--dim);text-align:left;align-self:flex-start}
.copy:hover{color:var(--text)}
.hist{display:flex;flex-direction:column;gap:8px}
.chart text{font-family:'JBM',monospace;font-size:9.5px;fill:var(--faint)}
.chart .gl{stroke:var(--line);stroke-width:1}
.chart .cl{stroke:var(--line-strong);stroke-width:1;stroke-dasharray:2 4}
.chart .ln{fill:none;stroke:var(--text);stroke-width:1.25}
.chart .ar{fill:rgba(237,237,237,.05)}
.sum{display:flex;gap:24px;align-items:center;font-size:11.5px}
.sum span{color:var(--dim)}
.sum b{font-family:'JBM',monospace;font-weight:400;margin-left:8px;color:var(--text)}
.sum .btn{margin-left:auto}
.launch{display:grid;grid-template-columns:240px 1fr;gap:20px;align-items:start}
.lstats{display:grid;grid-template-columns:1fr 1fr;border-top:1px solid var(--line)}
.lstats div{height:36px;border-bottom:1px solid var(--line);display:flex;flex-direction:column;justify-content:center;gap:2px}
.lstats div:nth-child(odd){padding-right:12px}
.lstats div:nth-child(even){padding-left:12px;border-left:1px solid var(--line)}
.lstats b{font-family:'JBM',monospace;font-weight:400;font-size:12px}
.lstats span{font-size:10.5px;color:var(--dim)}
'''
    main = f'''
<div class="switch">
<span class="seg"><button class="chip on">ХЕЙВЕН</button></span>
</div>
<div class="now">
<div class="cell"><b>{st("on")}в сети</b><button class="copy">ss14://95.31.51.216:1212 &#xEBCC;</button></div>
<div class="cell" style="width:88px">{count(42, 80)}<span>онлайн</span></div>
<div class="cell" style="width:96px"><b>Frontier</b><span>карта</span></div>
<div class="cell" style="width:88px"><b>Extended</b><span>режим</span></div>
<div class="cell" style="width:72px"><b>01:12</b><span>раунд</span></div>
<div class="cell" style="width:68px"><b>38 мс</b><span>пинг</span></div>
<a class="play" href="connecting.html">{PLAY}ИГРАТЬ</a>
</div>
<section class="hist">
<div class="cap"><span>ОНЛАЙН</span><span class="seg"><button class="chip on">24 ч</button><button class="chip">7 дней</button><button class="chip">месяц</button></span></div>
{chart(inner, 152, pts, 80, (0, 40, 80), ((0, '00'), (12, '06'), (24, '12'), (36, '18'), (48, '24')))}
<div class="sum"><span>пик<b>51</b></span><span>в среднем<b>27</b></span><span>доступность<b>99,6%</b></span><button class="btn">{I("EB84")}Таблица</button></div>
</section>
<section class="hist">
<div class="cap"><span>ЛАУНЧЕР</span><span></span></div>
<div class="launch">
<div class="lstats">
<div><b>73</b><span>сейчас</span></div>
<div><b>41</b><span>в игре</span></div>
<div><b>96</b><span>пик за сутки</span></div>
<div><b>188</b><span>рекорд</span></div>
</div>
{chart(inner - 260, 72, lp, None, (0, 50), ())}
</div>
</section>
'''
    page('monitoring.html', 'Мониторинг', 'monitoring.html', main, css)


monitoring_page()

# ---------------------------------------------------------------- news
def news_page():
    items = json.load(open(NEWS, encoding='utf-8-sig'))
    css = '''
.main{padding:0;flex-direction:row;gap:0}
.nlist{width:280px;flex-shrink:0;border-right:1px solid var(--line);display:flex;flex-direction:column}
.ni{padding:8px 16px;border-bottom:1px solid var(--line);display:flex;flex-direction:column;gap:4px;text-align:left}
.ni:hover{background:var(--hover)}
.ni.on{background:var(--panel)}
.ni .meta{display:flex;gap:8px;align-items:center;font-family:'JBM',monospace;font-size:10px;color:var(--dim)}
.ni .meta .icon{font-size:10px}
.ni b{font-size:12px;font-weight:500;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.art{flex-grow:1;padding:16px 24px;display:flex;flex-direction:column;gap:12px;max-width:600px}
.art .meta{display:flex;gap:12px;font-family:'JBM',monospace;font-size:10.5px;color:var(--dim)}
.art h1{margin:0;font-size:18px;line-height:1.25;font-weight:600}
.art p{margin:0;font-size:12.5px;line-height:1.6;color:var(--body)}
'''
    def meta(n):
        pin = I('EBA0') if n.get('pinned') else ''
        return f'<span>{n["date"]}</span><span>{n.get("tag", "").upper()}</span>{pin}'
    lst = '\n'.join(f'<button class="ni{" on" if i == 0 else ""}"><div class="meta">{meta(n)}</div><b>{n["title"]}</b></button>'
                    for i, n in enumerate(items))
    first = items[0]
    paras = ''.join(f'<p>{p.strip()}</p>' for p in first['body'].split('\n\n') if p.strip())
    main = f'''
<div class="nlist scroll">
{lst}
</div>
<article class="art scroll">
<div class="meta">{meta(first)}</div>
<h1>{first["title"]}</h1>
{paras}
</article>
'''
    page('news.html', 'Новости', 'news.html', main, css)


news_page()

# ---------------------------------------------------------------- settings
def settings_page():
    css = '''
.sets{max-width:600px;display:flex;flex-direction:column;gap:16px}
.srow{min-height:28px;display:grid;grid-template-columns:160px 1fr;gap:12px;align-items:center;border-bottom:1px solid var(--line);font-size:12px}
.srow > span:first-child{color:var(--dim)}
.ctl{display:flex;gap:4px;align-items:center;min-width:0}
.ctl .field{flex-grow:1}
.ctl .val{flex-grow:1;font-family:'JBM',monospace;font-size:11px}
.checks{display:flex;flex-direction:column;gap:8px;padding:8px 0;border-bottom:1px solid var(--line)}
.acts{display:flex;gap:8px;padding:8px 0;border-bottom:1px solid var(--line)}
'''
    def srow(label, ctl):
        return f'<div class="srow"><span>{label}</span><div class="ctl">{ctl}</div></div>'
    reset = '<button class="ibtn" aria-label="По умолчанию">&#xEAE2;</button>'
    chk = lambda t, on: f'<label class="check{" on" if on else ""}"><span class="box">{"&#xEAB2;" if on else ""}</span>{t}</label>'
    main = f'''
<div class="sets">
<section>
<div class="cap"><span>ФАЙЛЫ</span><span></span></div>
{srow("Папка", '<span class="val">~/.local/share/DarkHavenLauncher</span><button class="ibtn" aria-label="Открыть">&#xEA83;</button>')}
{srow("Кэш контента", '<span class="val">1,4 ГБ · 3 версии</span><button class="btn">Очистить</button>')}
<div class="acts"><button class="btn">Проверить кэш</button><button class="btn">Удалить старые движки</button><button class="btn">Собрать логи</button></div>
</section>
<section>
<div class="cap"><span>ПОВЕДЕНИЕ</span><span></span></div>
<div class="checks">
{chk("Обновлять контент автоматически", True)}
{chk("Сворачивать при запуске игры", True)}
{chk("Совместимость рендера (GLES2)", False)}
{chk("Подробный журнал", False)}
</div>
{srow("Лимит загрузки", '<label class="field" style="width:152px;flex-grow:0"><input class="mono" placeholder="—"><span class="suffix">КБ/с</span></label>')}
{srow("Обновления", '<button class="btn">Проверить</button>')}
</section>
<section>
<div class="cap"><span>ПОДКЛЮЧЕНИЕ</span><span></span></div>
{srow("Авторизация", f'<label class="field"><input class="mono" value="https://auth.spacestation14.com/"></label>{reset}')}
{srow("Сборки движка", f'<label class="field"><input class="mono" value="https://robust-builds.cdn.spacestation14.com/manifest.json"></label>{reset}')}
{srow("Список регионов", '<label class="field"><input class="mono" placeholder="—"></label>')}
{srow("Платформа", '<label class="field"><input class="mono" placeholder="https://api.darkhaven.games"></label>')}
</section>
<section>
<div class="cap"><span>DISCORD</span><span></span></div>
{srow("ID приложения", '<label class="field"><input class="mono" placeholder="—"></label>')}
</section>
</div>
'''
    page('settings.html', 'Настройки', 'settings.html', main, css, main_cls='main scroll')


settings_page()

# ---------------------------------------------------------------- account
def account_page():
    css = '''
.main{align-items:center;justify-content:center}
.form{width:260px;display:flex;flex-direction:column;gap:12px}
.fl{display:flex;flex-direction:column;gap:4px}
.links{display:flex;justify-content:space-between;font-size:11px;color:var(--dim)}
.links a:hover{color:var(--text)}
.saved{width:260px;margin-top:8px}
.acc{height:28px;display:grid;grid-template-columns:1fr auto auto auto;gap:12px;align-items:center;border-bottom:1px solid var(--line);font-size:12px}
.acc .mono{font-size:10px;color:var(--dim)}
.acc button{font-size:10.5px;color:var(--dim)}
.acc button:hover{color:var(--text)}
'''
    main = f'''
<form class="form" onsubmit="return false">
<label class="fl"><span class="lbl">ЛОГИН</span><span class="field"><input autocomplete="username"></span></label>
<label class="fl"><span class="lbl">ПАРОЛЬ</span><span class="field"><input type="password" autocomplete="current-password"></span></label>
<button class="btn primary wide" style="height:28px">ВОЙТИ</button>
<div class="links"><a href="#">Создать аккаунт</a><a href="#">Забыли пароль?</a></div>
</form>
<div class="saved">
<div class="cap"><span>АККАУНТЫ</span><span>2</span></div>
<div class="acc"><span>Kestrel</span><span class="mono">сохранён</span><button>Выбрать</button><button class="icon" aria-label="Выйти">&#xEA76;</button></div>
<div class="acc"><span>Kestrel_2</span><span class="mono">истёк</span><button>Выбрать</button><button class="icon" aria-label="Выйти">&#xEA76;</button></div>
</div>
'''
    page('account.html', 'Аккаунт', '', main, css, account_on=True)


account_page()

# ---------------------------------------------------------------- states sheet (reference for porting)
def states_page():
    css = '''
body{display:block;padding:32px 40px}
.sheet{width:880px;display:flex;flex-direction:column;gap:24px}
.sheet h2{margin:0 0 8px;font-family:'JBM',monospace;font-weight:400;font-size:10px;letter-spacing:.18em;color:var(--dim)}
.line{display:flex;align-items:center;gap:16px;flex-wrap:wrap}
.item{display:flex;flex-direction:column;gap:8px;align-items:flex-start}
.item small{font-family:'JBM',monospace;font-size:9.5px;color:var(--faint)}
.btn.h{border-color:var(--text)}
.btn.p{background:var(--press)}
.btn.primary.h{background:#FFFFFF}
.btn.primary.p{background:var(--body)}
.legend{display:flex;gap:24px;font-size:12px}
.legend span{display:flex;align-items:center;gap:8px}
.box3{width:200px;background:var(--bg);border:1px solid var(--line)}
.sbox{height:96px;padding:0 8px 0 12px}
.flash-static{background:var(--press)}
.verbox{width:144px;border:1px solid var(--line)}
'''
    btn_states = ''.join(f'<div class="item"><button class="btn {c}">Проверить</button><small>{l}</small></div>'
                         for c, l in [('', 'обычная'), ('h', 'наведение'), ('focus', 'фокус'), ('p', 'нажата'), ('disabled', 'недоступна')])
    pri_states = ''.join(f'<div class="item"><button class="btn primary {c}" style="width:128px">ИГРАТЬ</button><small>{l}</small></div>'
                         for c, l in [('', 'обычная'), ('h', 'наведение'), ('focus', 'фокус'), ('p', 'нажата')])
    scroll_rows = ''.join(f'<button class="row">{st("on")}<span>Сервер {i}</span><span class="mono">{10 + i}/40</span>{CHEV}</button>' for i in range(1, 10))
    html = f'''<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<title>Состояния</title>
<link rel="stylesheet" href="style.css">
<style>
{BASE_CSS.strip()}
{css.strip()}
</style>
</head>
<body>
<div class="sheet">
<section><h2>СТАТУС</h2>
<div class="legend"><span>{st("on")}в сети · готово · в игре</span><span>{st("q")}карантин · ждёт · в лаунчере</span><span>{st("full")}полный · идёт</span><span>{st("off")}офлайн · ошибка · не в сети</span></div>
</section>
<section><h2>КНОПКИ</h2>
<div class="line">{btn_states}</div>
<div class="line" style="margin-top:12px">{pri_states}</div>
</section>
<section><h2>ПОЛЯ И ПЕРЕКЛЮЧАТЕЛИ</h2>
<div class="line">
<label class="field" style="width:200px">{I("EA6D")}<input placeholder="Поиск"></label>
<label class="field" style="width:200px;border-color:var(--text)">{I("EA6D")}<input value="nova"></label>
<span class="seg"><button class="chip">нет</button><button class="chip on">лёгкий</button><button class="chip on">средний</button></span>
<label class="check on"><span class="box">&#xEAB2;</span>вкл</label>
<label class="check"><span class="box"></span>выкл</label>
</div>
</section>
<section><h2>ЗАГРУЗКА</h2>
<div class="box3"><div class="loading"></div>
<div class="skel"><i style="width:6px;margin-left:12px"></i><i style="width:96px"></i><i style="width:32px;margin-left:auto;margin-right:12px"></i></div>
<div class="skel"><i style="width:6px;margin-left:12px"></i><i style="width:72px"></i><i style="width:32px;margin-left:auto;margin-right:12px"></i></div>
<div class="skel"><i style="width:6px;margin-left:12px"></i><i style="width:112px"></i><i style="width:32px;margin-left:auto;margin-right:12px"></i></div>
</div>
</section>
<section><h2>ПРОКРУТКА · ИЗМЕНИЛОСЬ ЧИСЛО</h2>
<div class="line" style="align-items:flex-start">
<div class="box3 scroll sbox">{scroll_rows}</div>
<div class="item"><span class="mono flash-static" style="font-size:12px;padding:0 4px">43/80</span><small>подсветка 600 мс</small></div>
</div>
</section>
<section><h2>ВЕРСИЯ</h2>
<div class="line" style="align-items:flex-start">
<div class="verbox"><div class="ver"><div><span>ЛАУНЧЕР</span>0.2.4</div><div><span>ДВИЖОК</span>275.1.0</div></div></div>
<div class="verbox"><div class="ver"><div><span>ЛАУНЧЕР</span>0.2.4</div><div><span>ДВИЖОК</span>275.1.0</div><button class="upd">0.2.5 · обновить</button></div></div>
</div>
</section>
</div>
</body>
</html>
'''
    with open(os.path.join(OUT, 'states.html'), 'w') as f:
        f.write(html)


states_page()
print('ok')
