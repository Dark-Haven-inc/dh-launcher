# Бэкенд платформы Dark Haven — как его добавить

Лаунчер уже готов принять платформу. Вкладки **ГЛАВНАЯ / ПРОФИЛЬ / НОВОСТИ / АДМИН**,
уведомления, друзья, синк избранного, бан-на-лаунчер — всё это ждёт один HTTP-API, которого
пока нет. Этот документ — пошаговая инструкция, как его поднять.

---

## 1. Что уже есть и чего нет

| Есть | Нет |
|---|---|
| Игровой сервер пишет **PostgreSQL** (`player`, `play_time`, `server_ban`, `connection_log`, `admin`, `admin_log`) | HTTP-API, к которому стучится лаунчер |
| **SS14.Admin** — форк веб-панели визардов, ASP.NET, уже подключён к той БД | Домен (`darkhaven.*` — не зарегистрирован) |
| Аккаунты — визардовские (`auth.spacestation14.com`) | Своя таблица под профиль/новости/друзей/лаунчер-бан |
| Лаунчер трекает наигранное локально (`PlaySession`) | Discord-линк в профиле |

**Вывод:** нужен отдельный сервис — назовём `DarkHaven.Platform.Api`. Он:
- ведёт **свою** БД (профили, новости, лаунчер-баны, друзья, роли);
- **читает** игровую БД (только чтение) ради наигранного и игровых банов;
- проверяет визардовский токен игрока и выдаёт свой сессионный JWT.

```
┌──────────┐   SS14-токен    ┌─────────────────────┐   read-only    ┌──────────────┐
│ Лаунчер  │ ──────────────► │ DarkHaven.Platform  │ ─────────────► │  Игровая БД  │
│          │ ◄────── JWT ─── │        .Api         │                │ (PostgreSQL) │
└──────────┘   /api/*        │  + своя PostgreSQL  │                └──────────────┘
                             └─────────────────────┘
                                       ▲  проверка токена
                                       │
                             auth.spacestation14.com/api/auth/ping
```

---

## 2. Что нужно приготовить

1. **Сервер** — Linux + Docker. Проще всего рядом с игровым сервером / SS14.Admin.
2. **Домен** — например `darkhaven.games`, A-запись `api.darkhaven.games` → IP сервера.
   Это единственное, что нельзя сделать в коде. ~$10–15/год.
3. **PostgreSQL** — на игровом сервере он уже есть. Заводим **вторую базу** `dh_platform`
   (или отдельную схему) на том же инстансе.
4. **.NET 10 SDK** — для сборки.
5. **Строка подключения к игровой БД** (read-only пользователь) — у того, кто управляет сервером.
6. **Секрет для подписи JWT** — любая длинная случайная строка (`openssl rand -base64 48`).
7. *(опционально)* **Discord-приложение** — `discord.com/developers` → Client ID + Client Secret,
   redirect URI.

---

## 3. Создаём проект

```bash
mkdir dh-platform && cd dh-platform
dotnet new sln -n DarkHaven.Platform
dotnet new webapi -n DarkHaven.Platform.Api --use-controllers
dotnet sln add DarkHaven.Platform.Api

cd DarkHaven.Platform.Api
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add package Microsoft.EntityFrameworkCore.Design
dotnet add package Microsoft.AspNetCore.Authentication.JwtBearer
```

Отдельный репозиторий: `Dark-Haven-inc/dh-platform` (по аналогии с `dh-launcher`).

---

## 4. Модель своей БД

`Models/PlatformDb.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

public sealed class PlatformDb(DbContextOptions<PlatformDb> o) : DbContext(o)
{
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<NewsPost> News => Set<NewsPost>();
    public DbSet<LauncherBan> LauncherBans => Set<LauncherBan>();
    public DbSet<AdminRole> AdminRoles => Set<AdminRole>();
    public DbSet<Friendship> Friendships => Set<Friendship>();
    public DbSet<Notification> Notifications => Set<Notification>();
}

// Профиль привязан к визардовскому UserId (Guid) — это и есть личность игрока.
public sealed class Profile
{
    public Guid UserId { get; set; }            // PK, = Wizard's Den UserId
    public string Username { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public string Frame { get; set; } = "blue";
    public string? Title { get; set; }
    public string? DiscordId { get; set; }
    public string? DiscordAvatar { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}

public sealed class NewsPost
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string BodyMarkdown { get; set; } = "";
    public string? ImageUrl { get; set; }
    public Guid AuthorId { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
    public bool Draft { get; set; }
}

// Бан-на-лаунчер: блокирует подключение ко всем серверам DH (см. §8).
public sealed class LauncherBan
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public string Reason { get; set; } = "";
    public Guid IssuedBy { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }   // null = навсегда
}

public sealed class AdminRole
{
    public Guid UserId { get; set; }             // PK
    public string Role { get; set; } = "";       // "owner" | "admin" | "news"
}

public sealed class Friendship
{
    public int Id { get; set; }
    public Guid RequesterId { get; set; }
    public Guid AddresseeId { get; set; }
    public bool Accepted { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Notification
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public string Kind { get; set; } = "";       // "ban" | "warning" | "news"
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public bool Read { get; set; }
}
```

Миграция:

```bash
dotnet ef migrations add Initial
dotnet ef database update
```

### Чтение игровой БД

Не скаффолдим всю схему SS14 — берём три таблицы через «сырой» запрос:

```csharp
public sealed class GameDbReader(NpgsqlDataSource ds)
{
    public async Task<long> TotalPlaytimeSecondsAsync(Guid userId)
    {
        await using var cmd = ds.CreateCommand("""
            SELECT COALESCE(EXTRACT(EPOCH FROM SUM(time_spent)), 0)
            FROM play_time pt JOIN player p ON p.player_id = pt.player_id
            WHERE p.user_id = $1 AND pt.tracker = 'Overall'
            """);
        cmd.Parameters.AddWithValue(userId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task<IReadOnlyList<(string Reason, DateTimeOffset At)>> RecentBansAsync(Guid userId)
    {
        // server_ban: player_user_id, reason, ban_time, expiration_time
        // ... аналогичный запрос
    }
}
```

> Наигранное **по серверам** в ванильной схеме SS14 не хранится (`play_time` — по трекерам,
> не по серверам). Варианты: (а) лаунчер шлёт свои `PlaySession` в API через
> `POST /api/playtime/session`; (б) пропатчить игровой сервер, чтобы писал сессии в свою
> таблицу. Пока — вариант (а), данные уже собираются лаунчером.

---

## 5. Авторизация

Лаунчер уже держит визардовский токен игрока. Пусть меняет его на наш JWT.

`Auth/SessionController.cs`:

```csharp
[ApiController, Route("api/session")]
public sealed class SessionController(PlatformDb db, IHttpClientFactory http, IConfiguration cfg) : ControllerBase
{
    // Лаунчер: POST /api/session, заголовок  Authorization: SS14Auth <wizards-token>
    [HttpPost]
    public async Task<IActionResult> Create()
    {
        var token = Request.Headers.Authorization.ToString();
        if (!token.StartsWith("SS14Auth ")) return Unauthorized();

        // 1. проверяем токен у визардов
        var client = http.CreateClient();
        var ping = new HttpRequestMessage(HttpMethod.Get, "https://auth.spacestation14.com/api/auth/ping");
        ping.Headers.Add("Authorization", token);
        var res = await client.SendAsync(ping);
        if (!res.IsSuccessStatusCode) return Unauthorized();

        var me = await res.Content.ReadFromJsonAsync<PingResponse>();   // { userId, userName }

        // 2. апсертим профиль
        var profile = await db.Profiles.FindAsync(me!.UserId)
                      ?? db.Profiles.Add(new Profile { UserId = me.UserId, FirstSeen = DateTimeOffset.UtcNow }).Entity;
        profile.Username = me.UserName;
        profile.LastSeen = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        // 3. выдаём свой JWT на 1 час
        var roles = await db.AdminRoles.Where(r => r.UserId == me.UserId).Select(r => r.Role).ToArrayAsync();
        var jwt = JwtHelper.Issue(me.UserId, me.UserName, roles, cfg["Jwt:Secret"]!);
        return Ok(new { token = jwt, expiresIn = 3600 });
    }

    private sealed record PingResponse(Guid UserId, string UserName);
}
```

`Program.cs` — включаем JWT-проверку:

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new()
    {
        ValidateIssuer = false, ValidateAudience = false,
        IssuerSigningKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Secret"]!)),
    };
});
app.UseAuthentication();
app.UseAuthorization();
```

Дальше в контроллерах `[Authorize]`, а `User.FindFirst("sub")` даёт `UserId`.

---

## 6. Эндпоинты (минимум для оживления вкладок)

```csharp
[Authorize, ApiController, Route("api/profile")]
public sealed class ProfileController(PlatformDb db, GameDbReader game, SettingsPlaytime local) : ControllerBase
{
    [HttpGet("me")]
    public async Task<object> Me()
    {
        var id = User.Guid();
        var p = await db.Profiles.FindAsync(id);
        return new
        {
            p!.Username, p.AvatarUrl, p.Frame, p.Title, p.DiscordId, p.DiscordAvatar,
            memberSince = p.FirstSeen,
            totalPlaytimeSeconds = await game.TotalPlaytimeSecondsAsync(id),
            launcherBanned = await db.LauncherBans.AnyAsync(b => b.UserId == id &&
                (b.ExpiresAt == null || b.ExpiresAt > DateTimeOffset.UtcNow)),
        };
    }

    [HttpPut("me")]           // смена рамки / титула / аватара
    public async Task<IActionResult> Update([FromBody] ProfilePatch patch) { /* ... */ return NoContent(); }
}

[ApiController, Route("api/news")]
public sealed class NewsController(PlatformDb db) : ControllerBase
{
    [HttpGet]  // публично, без авторизации
    public async Task<object> List() =>
        await db.News.Where(n => !n.Draft).OrderByDescending(n => n.PublishedAt).Take(30).ToListAsync();
}

[Authorize, ApiController, Route("api/notifications")]
public sealed class NotificationsController(PlatformDb db) : ControllerBase
{
    [HttpGet]
    public async Task<object> List() =>
        await db.Notifications.Where(n => n.UserId == User.Guid() && !n.Read)
                              .OrderByDescending(n => n.CreatedAt).ToListAsync();
}

// Проверка бана — вызывается ИГРОВЫМ сервером на подключении (см. §8), без JWT, по секрету
[ApiController, Route("api/bans")]
public sealed class BansController(PlatformDb db, IConfiguration cfg) : ControllerBase
{
    [HttpGet("check")]
    public async Task<IActionResult> Check([FromQuery] Guid userId, [FromHeader(Name = "X-DH-Secret")] string secret)
    {
        if (secret != cfg["ServerSecret"]) return Unauthorized();
        var ban = await db.LauncherBans.FirstOrDefaultAsync(b => b.UserId == userId &&
            (b.ExpiresAt == null || b.ExpiresAt > DateTimeOffset.UtcNow));
        return ban is null ? Ok(new { banned = false }) : Ok(new { banned = true, ban.Reason });
    }
}
```

Админские (`[Authorize(Roles = "admin,owner")]`): `POST /api/admin/news`, `POST /api/admin/launcher-ban`,
`GET/POST /api/admin/roles`, `POST /api/admin/warning`.

Друзья: `GET /api/friends`, `POST /api/friends/request?userId=`, `POST /api/friends/accept?id=`.
Чат — это уже WebSocket (`app.MapHub<ChatHub>("/hub/chat")` через SignalR), отдельная фаза.

---

## 7. Подключаем лаунчер

Новый клиент в `src/DarkHaven.Launcher/Api/PlatformApi.cs`:

```csharp
public sealed class PlatformApi(HttpClient http, string baseUrl)
{
    private string? _jwt;

    public async Task<bool> SignInAsync(string ss14Token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/session");
        req.Headers.Add("Authorization", $"SS14Auth {ss14Token}");
        var res = await http.SendAsync(req);
        if (!res.IsSuccessStatusCode) return false;
        _jwt = (await res.Content.ReadFromJsonAsync<SessionResponse>())!.Token;
        return true;
    }

    public async Task<ProfileDto?> GetProfileAsync()
        => await Get<ProfileDto>("/api/profile/me");

    private async Task<T?> Get<T>(string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
        if (_jwt is not null) req.Headers.Add("Authorization", $"Bearer {_jwt}");
        var res = await http.SendAsync(req);
        return res.IsSuccessStatusCode ? await res.Content.ReadFromJsonAsync<T>() : default;
    }
}
```

- `AppServices`: `Platform = new PlatformApi(Http, Settings.GetConfig("PlatformApiUrl") ?? "https://api.darkhaven.games");`
- Поле «Platform API» в Настройках (по аналогии с auth-сервером) — чтобы можно было переключать.
- `ProfileViewModel`, `NewsViewModel`, `AdminViewModel` начинают дёргать `Platform.*` вместо заглушек.
- Пока `PlatformApiUrl` пуст или сервис недоступен — остаётся текущее локальное поведение.

---

## 8. Бан-на-лаунчер — честно

«Не подключиться **ни к одному** серверу» лаунчер сам обеспечить не может — игрок просто
запустит игру без лаунчера. Нужен один из двух вариантов:

**A. Проверка на стороне игрового сервера (проще).**
Небольшой патч в `dh-sector-frontier`: обработчик `IConnecting` / событие подключения дёргает
`GET https://api.darkhaven.games/api/bans/check?userId=<guid>` с заголовком `X-DH-Secret` и
отклоняет с причиной. Ставится на **каждый** сервер DH. Это ~30 строк server-side кода.

**B. Свой auth-сервер (радикально, но железно).**
DH поднимает сервис авторизации SS14 → он не выдаёт токен забаненному → игрок не заходит
никуда, где стоит DH-auth. Это отдельный большой проект (см. разговор про auth в журнале).

Рекомендация: **A** сейчас, **B** — если платформа станет обязательной.

---

## 9. Deploy

`Dockerfile`:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
COPY . .
RUN dotnet publish DarkHaven.Platform.Api -c Release -o /out
FROM base
COPY --from=build /out .
ENTRYPOINT ["dotnet", "DarkHaven.Platform.Api.dll"]
```

`docker-compose.yml` (рядом с игровым сервером):

```yaml
services:
  dh-platform:
    build: .
    restart: unless-stopped
    environment:
      ConnectionStrings__Platform: "Host=postgres;Database=dh_platform;Username=dh;Password=..."
      ConnectionStrings__Game: "Host=postgres;Database=ss14;Username=dh_ro;Password=...;"
      Jwt__Secret: "<openssl rand -base64 48>"
      ServerSecret: "<общий секрет с игровым сервером>"
    ports: ["127.0.0.1:8081:8080"]
```

nginx + Let's Encrypt:

```nginx
server {
    server_name api.darkhaven.games;
    location / { proxy_pass http://127.0.0.1:8081; proxy_set_header Host $host; }
}
# затем:  certbot --nginx -d api.darkhaven.games
```

---

## 10. Порядок работ

| Шаг | Что | Оживает |
|---|---|---|
| 0 | Домен + сервер + пустая БД | — |
| 1 | Проект + `PlatformDb` + миграция | — |
| 2 | `/api/session` (обмен токена на JWT) | вход в платформу |
| 3 | `/api/profile/me` + чтение `play_time` из игровой БД | **ПРОФИЛЬ** (наигранное с сервера) |
| 4 | `/api/news` + `/api/admin/news` | **НОВОСТИ** + редактор |
| 5 | `LauncherBan` + `/api/bans/check` + патч игрового сервера | **бан-на-лаунчер** работает |
| 6 | `/api/notifications` + запись при бане/варне | уведомления в лаунчере |
| 7 | `AdminRole` + админ-эндпоинты + UI вкладки АДМИН | **АДМИН** |
| 8 | `Friendship` + эндпоинты | друзья |
| 9 | SignalR-хаб | чат |
| 10 | Discord OAuth → линк в профиль | аватар/ник Discord |

**MVP (шаги 0–5): ~1–2 недели одному C#-разработчику.** Полный набор: +3–5 недель.

---

## 11. Что должен решить командир

- [ ] **Домен** — какой (`darkhaven.games`? `dark-haven.gg`?), кто регистрирует
- [ ] **Где деплой** — тот же сервер, что игровой / SS14.Admin? отдельный VPS?
- [ ] **Кто пишет** — я могу, но это отдельный репозиторий и ~2 недели на MVP
- [ ] **Auth-модель** — визарды + Discord-линк (вариант B), или свой auth (вариант C)
- [ ] **Публичный ли** репозиторий `dh-platform`
- [ ] **Read-only доступ к игровой БД** — строка подключения от того, кто держит сервер
- [ ] **Discord-приложение** — создать для OAuth (Client ID + Secret)

Как эти галочки закрыты — начинаю с MVP, и **ПРОФИЛЬ / НОВОСТИ / АДМИН** в лаунчере
перестают быть заглушками.
