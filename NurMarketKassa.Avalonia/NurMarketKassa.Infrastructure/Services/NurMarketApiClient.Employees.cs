using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models.Auth;
using NurMarketKassa.Models;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

// 2026-11-06: В15 — часть класса NurMarketApiClient (разбор огромного файла на partial-файлы), логика не менялась; остальное — NurMarketApiClient.cs
public sealed partial class NurMarketApiClient
{
    /// <summary>2026-09-08: список сотрудников компании (app.nurcrm.kg, раздел "Сотрудники") —
    /// нужен, чтобы в кассе (Настройки → Сотрудники) не вводить имена вручную, а подставлять
    /// реальных сотрудников с сайта. Точный URL не подтверждён (нет доступа к серверному коду) —
    /// пробуем несколько правдоподобных вариантов подряд, как уже сделано для CartDeleteCode/
    /// MaxDiscountPercent. Первый эндпоинт, который ответит успехом (даже пустым списком),
    /// используется; если все вернут 404 — возвращаем null (вызывающий код должен явно
    /// сообщить пользователю, что подключение не удалось).</summary>
    private static readonly string[] EmployeeCandidatePaths =
    [
        "api/users/employees/",
        "api/users/staff/",
        "api/users/company/employees/",
        "api/users/company/staff/",
        "api/users/",
    ];

    private static readonly string[] RoleCandidatePaths =
    [
        "api/users/roles/",
        "api/users/company/roles/",
        "api/main/roles/",
    ];

    /// <summary>2026-10-28, жалоба владельца «создание/удаление/правка ролей - неправильный API»: по схеме сервера (openapi 26.10)
    /// список ролей - GET users/roles/, а создание (POST), правка (PUT/PATCH) и удаление (DELETE) - users/roles/custom/ и
    /// users/roles/custom/{id}/. Раньше сначала шли users/roles/, users/company/roles/, main/roles/ - они для записи не предназначены.
    /// Теперь адрес по схеме первый, прежние - запасные.</summary>
    private static readonly string[] RoleWritePaths =
    [
        "api/users/roles/custom/",
        "api/users/roles/",
        "api/users/company/roles/",
        "api/main/roles/",
    ];

    /// <summary>404 с JSON-телом ({"detail":"Not found."}) - сервер адрес нашёл, но объекта нет; другие адреса пробовать незачем.</summary>
    private static bool IsObjectNotFound(ApiException ex) => ex.StatusCode == 404 && ex.Payload is { ValueKind: JsonValueKind.Object };

    public async Task<List<EmployeeInfoDto>?> GetEmployeesAsync(CancellationToken ct = default)
    {
        foreach (var path in EmployeeCandidatePaths)
        {
            JsonElement data;
            try
            {
                // 2026-10-28: страница по 200 (схема: count/next/results; page_size как у ListConsultantsAsync).
                data = await RequestAsync(HttpMethod.Get, path, null, new Dictionary<string, string> { ["page_size"] = "200" }, ct).ConfigureAwait(false);
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 405)
            {
                continue;
            }

            var employees = ParseEmployeeList(data);
            if (employees != null)
            {
                // 2026-10-28, аудит: раньше читалась только первая страница — при сотрудниках больше размера страницы остальные
                // «пропадали» (не видны в «Сотрудниках», «Загрузить с сайта», сверке кодов, именах актов склада; проверка удаления
                // «ещё в списке» могла ошибиться). Идём по next до конца (не больше 30 страниц), повторы по id отбрасываем.
                var seen = new HashSet<string>(employees.Where(e => e.Id != null).Select(e => e.Id!), StringComparer.OrdinalIgnoreCase);
                var next = NextPageQuery(data);
                for (var page = 0; page < 30 && next != null; page++)
                {
                    JsonElement more;
                    try
                    {
                        more = await RequestAsync(HttpMethod.Get, path, null, next, ct).ConfigureAwait(false);
                    }
                    catch (ApiException ex) when (ex.StatusCode is 404)
                    {
                        break;
                    }
                    var extra = ParseEmployeeList(more);
                    if (extra == null)
                        break;
                    foreach (var e in extra)
                        if (e.Id == null || seen.Add(e.Id))
                            employees.Add(e);
                    next = NextPageQuery(more);
                }
                PosLogger.Log($"Employees loaded from {path}: {employees.Count}", "API");
                EmployeeDirectory.Update(employees);
                return employees;
            }
        }

        PosLogger.Log("Employees: none of the candidate endpoints returned a recognizable list", "API");
        return null;
    }

    /// <summary>2026-09-08: подтверждено через DevTools (владелец прислал Network-лог реального
    /// создания сотрудника на сайте): POST на ОТДЕЛЬНЫЙ путь "create/" (не на сам список — сайт
    /// здесь не следует чистому REST), роль передаётся как "custom_role" (не "role"), пароль
    /// сотрудник НЕ вводит — сервер генерирует его сам и возвращает в ответе (сайт сразу
    /// показывает "Логин сотрудника" с логином/паролем). Права can_view_* сайт тоже отправляет
    /// явно при создании.
    /// 2026-09-21: раньше здесь все can_view_* шли жёстко false — форма "Новый сотрудник" в
    /// кассе вообще не давала их выбрать (жалоба владельца: "на сайте при создании сотрудника
    /// есть доступы а у нас нет"). Полный список полей подтверждён живым GET
    /// api/users/employees/ на реальном аккаунте — см. doc-comment у EmployeeAccessFlags.</summary>
    private static readonly string[] EmployeeCreatePaths =
    [
        "api/users/employees/create/",
        "api/users/employees/",
    ];

    public Task<EmployeeInfoDto?> CreateEmployeeAsync(
        string email, string firstName, string lastName, string roleId, EmployeeAccessFlags? access = null, CancellationToken ct = default) =>
        CreateEmployeeWithExtrasAsync(email, firstName, lastName, roleId, access, null, ct);

    public async Task<EmployeeInfoDto?> CreateEmployeeWithExtrasAsync(
        string email, string firstName, string lastName, string roleId, EmployeeAccessFlags? access,
        IReadOnlyDictionary<string, object?>? extraFields, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>
        {
            ["email"] = email.Trim(),
            ["first_name"] = firstName.Trim(),
            ["last_name"] = lastName.Trim(),
            ["phone_number"] = "",
            ["track_number"] = "",
            ["custom_role"] = roleId,
        };
        foreach (var kv in (access ?? new EmployeeAccessFlags()).ToRequestFields())
            body[kv.Key] = kv.Value;
        // 2026-10-28: телефон, номер и т. п. из формы «Новый сотрудник» (EmployeeCreate в схеме) перекрывают пустые значения.
        if (extraFields != null)
            foreach (var kv in extraFields)
                if (kv.Value != null)
                    body[kv.Key] = kv.Value;
        EmployeeDirectory.Invalidate();

        foreach (var path in EmployeeCreatePaths)
        {
            JsonElement data;
            try
            {
                data = await RequestAsync(HttpMethod.Post, path, body, null, ct).ConfigureAwait(false);
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 405)
            {
                continue;
            }

            var created = ParseEmployeeList(WrapAsSingleElementArray(data))?.FirstOrDefault()
                ?? new EmployeeInfoDto { Email = email, FullName = $"{firstName} {lastName}".Trim() };
            // 2026-10-18, аудит: id в ответе на создание не разбирался (живая проверка) — у новой карточки не было
            // «Доступов», а удаление не шло на сервер. Ищем id во вложенном объекте или числом, иначе — в списке
            // сотрудников по email (как в «Загрузить с сайта»).
            created.Id ??= FindEmployeeIdDeep(data, 0);
            if (string.IsNullOrWhiteSpace(created.Id))
            {
                try
                {
                    var all = await GetEmployeesAsync(ct).ConfigureAwait(false);
                    created.Id = all?.FirstOrDefault(e => string.Equals(e.Email?.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;
                }
                catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
                {
                    PosLogger.Log($"Employee created, id not found in list: {ex.GetType().Name}", "API");
                }
            }
            PosLogger.Log($"Employee created via {path} (password returned: {created.Password != null}, id: {(created.Id != null ? "yes" : "no")})", "API");
            return created;
        }

        throw new ApiException("Не найден рабочий адрес API для создания сотрудника.", 404);
    }

    /// <summary>2026-09-21, по просьбе владельца ("клик по сотруднику открывал редактор
    /// доступов, как на сайте"): изменить доступы (can_view_*) уже созданного сотрудника.
    /// Точный путь/метод НЕ подтверждён живым запросом (только GET-список и создание —
    /// см. doc-comment у CreateEmployeeAsync/EmployeeCreatePaths), в отличие от них — пробуем
    /// несколько правдоподобных REST-вариантов подряд, как уже сделано для
    /// DeleteEmployeeAsync. Роль/email/имя здесь намеренно не трогаем — шлём только
    /// can_view_* поля, чтобы случайно не затереть остальные данные сотрудника, если сервер
    /// не поддерживает частичный PATCH и требует все поля разом (тогда этот путь просто
    /// вернёт ошибку валидации, а не тихо испортит данные).</summary>
    public async Task UpdateEmployeeAccessAsync(string employeeId, EmployeeAccessFlags access, CancellationToken ct = default)
    {
        var body = access.ToRequestFields();
        string[] updateCandidates =
        [
            $"api/users/employees/{employeeId}/",
            $"api/users/employees/{employeeId}/update/",
            $"api/users/employees/update/{employeeId}/",
        ];

        foreach (var path in updateCandidates)
        {
            try
            {
                await RequestAsync(HttpMethod.Patch, path, body, null, ct).ConfigureAwait(false);
                PosLogger.Log($"Employee access updated via {path}", "API");
                return;
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 405)
            {
                continue;
            }
        }

        throw new ApiException("Не найден рабочий адрес API для изменения доступов сотрудника.", 404);
    }

    /// <summary>2026-10-28: PATCH только переданных (изменённых) полей сотрудника — имя, телефон, номер, роль, филиалы, флаги.
    /// Неизменённые поля не отправляются, поэтому правка на другом устройстве (программа владельца ↔ касса ↔ сайт ↔ мобилка)
    /// не затирается. Адрес по схеме сервера — api/users/employees/{id}/ (PATCH), запасные — как у UpdateEmployeeAccessAsync.
    /// Возвращает сотрудника из ответа (если сервер его вернул).</summary>
    public async Task<EmployeeInfoDto?> PatchEmployeeAsync(string employeeId, IReadOnlyDictionary<string, object?> changes, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(employeeId))
            throw new ApiException("Не указан сотрудник.", 400);
        if (changes.Count == 0)
            return null;
        var body = changes.ToDictionary(kv => kv.Key, kv => kv.Value);
        var id = Uri.EscapeDataString(employeeId.Trim());
        string[] candidates =
        [
            $"api/users/employees/{id}/",
            $"api/users/employees/{id}/update/",
            $"api/users/employees/update/{id}/",
        ];
        foreach (var path in candidates)
        {
            try
            {
                var data = await RequestAsync(HttpMethod.Patch, path, body, null, ct).ConfigureAwait(false);
                var flagCount = body.Keys.Count(k => k.StartsWith("can_", StringComparison.Ordinal));
                PosLogger.Log($"Employee patched via {path}: fields {string.Join(",", body.Keys.Where(k => !k.StartsWith("can_", StringComparison.Ordinal)))}, access flags {flagCount}", "API");
                EmployeeDirectory.Invalidate();
                return ParseEmployeeList(WrapAsSingleElementArray(data))?.FirstOrDefault();
            }
            // 404 с JSON ({"detail":"Not found."}) - такого сотрудника нет (удалён на сайте), а не «адрес не найден»: пробовать другие адреса незачем.
            catch (ApiException ex) when (ex.StatusCode == 404 && ex.Payload is { ValueKind: JsonValueKind.Object })
            {
                throw;
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 405)
            {
                continue;
            }
        }

        throw new ApiException("Не найден рабочий адрес API для изменения сотрудника.", 404);
    }

    /// <summary>2026-09-08: сервер отвечает "успехом" (2xx) на DELETE, но сотрудник иногда
    /// остаётся в списке (по докладу владельца — "удаляет, но при обновлении возвращается").
    /// Раз статус-код 2xx недостаточен как доказательство, перечитываем список сразу после
    /// удаления и сверяем, что id действительно пропал — если нет, честно сообщаем об этом
    /// вызывающему коду вместо того, чтобы врать об успехе. Раз создание сотрудника оказалось
    /// НЕ на чистом REST-пути (отдельный "create/", см. CreateEmployeeAsync), удаление тоже
    /// может быть не по шаблону "DELETE .../{id}/" — пробуем несколько правдоподобных
    /// вариантов полного пути, не только через EmployeeCandidatePaths.</summary>
    public async Task DeleteEmployeeAsync(string employeeId, CancellationToken ct = default)
    {
        string[] deleteCandidates =
        [
            $"api/users/employees/{employeeId}/",
            $"api/users/employees/{employeeId}/delete/",
            $"api/users/employees/delete/{employeeId}/",
        ];

        var deletedVia = "";
        foreach (var path in deleteCandidates)
        {
            try
            {
                await RequestAsync(HttpMethod.Delete, path, null, null, ct).ConfigureAwait(false);
                deletedVia = path;
                break;
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 405)
            {
                continue;
            }
        }

        if (deletedVia.Length == 0)
            throw new ApiException("Не найден рабочий адрес API для удаления сотрудника.", 404);

        PosLogger.Log($"Employee delete request accepted via {deletedVia}, verifying...", "API");
        var stillThere = await GetEmployeesAsync(ct).ConfigureAwait(false);
        if (stillThere != null && stillThere.Any(e => string.Equals(e.Id, employeeId, StringComparison.Ordinal)))
        {
            PosLogger.Log($"Employee delete via {deletedVia} reported success but employee is still on the server", "API");
            throw new ApiException(
                "Сервер принял запрос на удаление, но сотрудник всё ещё числится в списке компании — похоже, это не тот адрес API " +
                "(возможно, он только отключает доступ, а не удаляет запись на сайте). Удалите сотрудника на сайте app.nurcrm.kg напрямую.",
                409);
        }

        EmployeeDirectory.Invalidate();
        PosLogger.Log($"Employee deleted via {deletedVia} (verified: no longer in the list)", "API");
    }

    public async Task<List<RoleInfoDto>?> GetRolesAsync(CancellationToken ct = default)
    {
        foreach (var path in RoleCandidatePaths)
        {
            JsonElement data;
            try
            {
                // 2026-10-28: page_size — роли тоже отдаются страницами (схема: count/next/results).
                data = await RequestAsync(HttpMethod.Get, path, null, new Dictionary<string, string> { ["page_size"] = "200" }, ct).ConfigureAwait(false);
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 405)
            {
                continue;
            }

            var roles = ParseRoleList(data);
            if (roles != null)
            {
                PosLogger.Log($"Roles loaded from {path}: {roles.Count}", "API");
                return roles;
            }
        }

        PosLogger.Log("Roles: none of the candidate endpoints returned a recognizable list", "API");
        return null;
    }

    /// <summary>2026-09-08: создать роль (форма "Новая роль": одно поле "Название роли").
    /// Тело запроса {"name": ...} — по тому же соглашению, что уже используют реальные
    /// CreateCategoryAsync/CreateBrandAsync (api/main/categories|brands/, тоже {"name": ...}).</summary>
    public async Task<RoleInfoDto?> CreateRoleAsync(string name, CancellationToken ct = default)
    {
        var body = new Dictionary<string, string> { ["name"] = name.Trim() };

        foreach (var path in RoleWritePaths)
        {
            JsonElement data;
            try
            {
                data = await RequestAsync(HttpMethod.Post, path, body, null, ct).ConfigureAwait(false);
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 405)
            {
                continue;
            }

            var created = ParseRoleList(WrapAsSingleElementArray(data))?.FirstOrDefault()
                ?? new RoleInfoDto { Name = name };
            PosLogger.Log($"Role created via {path}", "API");
            return created;
        }

        throw new ApiException("Не найден рабочий адрес API для создания роли.", 404);
    }

    /// <summary>2026-10-28: переименовать роль - PATCH users/roles/custom/{id}/ {"name"} (по схеме сервера; в CustomRole других полей нет:
    /// права роли в ней не хранятся, их задают флагами сотрудника).</summary>
    public async Task<RoleInfoDto?> UpdateRoleAsync(string roleId, string name, CancellationToken ct = default)
    {
        var body = new Dictionary<string, string> { ["name"] = name.Trim() };
        foreach (var path in RoleWritePaths)
        {
            try
            {
                var data = await RequestAsync(HttpMethod.Patch, $"{path}{Uri.EscapeDataString(roleId)}/", body, null, ct).ConfigureAwait(false);
                PosLogger.Log($"Role renamed via {path}{{id}}/", "API");
                return ParseRoleList(WrapAsSingleElementArray(data))?.FirstOrDefault() ?? new RoleInfoDto { Id = roleId, Name = name.Trim() };
            }
            catch (ApiException ex) when (IsObjectNotFound(ex))
            {
                throw;
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 405)
            {
                continue;
            }
        }

        throw new ApiException("Не найден рабочий адрес API для изменения роли.", 404);
    }

    /// <summary>2026-09-08: та же проверка "после удаления", что и в DeleteEmployeeAsync —
    /// см. её комментарий.</summary>
    public async Task DeleteRoleAsync(string roleId, CancellationToken ct = default)
    {
        var deletedByPath = "";
        foreach (var path in RoleWritePaths)
        {
            try
            {
                await RequestAsync(HttpMethod.Delete, $"{path}{Uri.EscapeDataString(roleId)}/", null, null, ct).ConfigureAwait(false);
                deletedByPath = path;
                break;
            }
            catch (ApiException ex) when (IsObjectNotFound(ex))
            {
                throw;
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 405)
            {
                continue;
            }
        }

        if (deletedByPath.Length == 0)
            throw new ApiException("Не найден рабочий адрес API для удаления роли.", 404);

        PosLogger.Log($"Role delete request accepted via {deletedByPath}{roleId}/, verifying...", "API");
        var stillThere = await GetRolesAsync(ct).ConfigureAwait(false);
        if (stillThere != null && stillThere.Any(r => string.Equals(r.Id, roleId, StringComparison.Ordinal)))
        {
            PosLogger.Log($"Role delete via {deletedByPath}{roleId}/ reported success but role is still on the server", "API");
            throw new ApiException(
                "Сервер принял запрос на удаление, но роль всё ещё числится в списке — похоже, это не тот адрес API. " +
                "Удалите роль на сайте app.nurcrm.kg напрямую.",
                409);
        }

        PosLogger.Log($"Role deleted via {deletedByPath}{roleId}/ (verified: no longer in the list)", "API");
    }

    /// <summary>2026-10-18: id сотрудника в ответе на создание — верхний уровень или вложенный объект
    /// (user/employee/data/result), строкой или числом.</summary>
    private static string? FindEmployeeIdDeep(JsonElement obj, int depth)
    {
        if (obj.ValueKind != JsonValueKind.Object || depth > 3)
            return null;
        foreach (var name in new[] { "id", "user_id", "uuid", "pk" })
        {
            if (!obj.TryGetProperty(name, out var v))
                continue;
            if (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                return v.GetString()!.Trim();
            if (v.ValueKind == JsonValueKind.Number)
                return v.GetRawText();
        }
        foreach (var name in new[] { "user", "employee", "data", "result" })
            if (obj.TryGetProperty(name, out var nested) && FindEmployeeIdDeep(nested, depth + 1) is { } id)
                return id;
        return null;
    }

    /// <summary>Оборачивает одиночный объект (ответ на POST) в массив из одного элемента, чтобы
    /// переиспользовать ParseEmployeeList/ParseRoleList, написанные для списков.</summary>
    private static JsonElement WrapAsSingleElementArray(JsonElement single)
    {
        if (single.ValueKind == JsonValueKind.Object && single.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Object)
            single = inner;
        if (single.ValueKind != JsonValueKind.Object)
            return default;

        using var doc = JsonDocument.Parse("[" + single.GetRawText() + "]");
        return doc.RootElement.Clone();
    }

    private static List<RoleInfoDto>? ParseRoleList(JsonElement root)
    {
        var data = root;
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("results", out var results))
            data = results;
        else if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out var inner))
            data = inner;

        if (data.ValueKind != JsonValueKind.Array)
            return null;

        var list = new List<RoleInfoDto>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            list.Add(new RoleInfoDto
            {
                Id = ReadTopLevelStringAny(item, "id", "role_id", "uuid"),
                Name = ReadTopLevelStringAny(item, "name", "role_name", "title"),
            });
        }

        return list;
    }

    private static List<EmployeeInfoDto>? ParseEmployeeList(JsonElement root)
    {
        var data = root;
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("results", out var results))
            data = results;
        else if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out var inner))
            data = inner;

        if (data.ValueKind != JsonValueKind.Array)
            return null;

        var list = new List<EmployeeInfoDto>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            string? firstName = ReadTopLevelStringAny(item, "first_name", "firstName");
            string? lastName = ReadTopLevelStringAny(item, "last_name", "lastName");
            string? fullName = ReadTopLevelStringAny(item, "full_name", "fullName", "name", "fio", "display_name")
                ?? (string.IsNullOrWhiteSpace(firstName) && string.IsNullOrWhiteSpace(lastName)
                    ? null
                    : $"{firstName} {lastName}".Trim());

            var dto = new EmployeeInfoDto
            {
                Id = ReadTopLevelStringAny(item, "id", "user_id", "uuid"),
                FullName = fullName,
                Email = ReadTopLevelStringAny(item, "email"),
                Phone = ReadTopLevelStringAny(item, "phone", "phone_number"),
                // 2026-09-21: "role" у сотрудника почти всегда null, реальное имя роли лежит в
                // "role_display" (подтверждено живым GET) — раньше этого варианта не было в
                // списке, и RoleName оставался пустым для всех сотрудников.
                RoleName = ReadTopLevelStringAny(item, "role_display", "role_name", "role", "position", "user_role"),
                RoleId = ReadTopLevelStringAny(item, "custom_role", "role_id"),
                Password = ReadTopLevelStringAny(item, "password", "generated_password", "temp_password", "default_password"),
                Access = ReadEmployeeAccessFlags(item),
                // 2026-10-28: остальные поля схемы сотрудника (EmployeeUpdate/UserList).
                FirstName = firstName,
                LastName = lastName,
                TrackNumber = ReadTopLevelString(item, "track_number"),
                AvatarUrl = ReadTopLevelString(item, "avatar"),
                RoleCode = ReadTopLevelString(item, "role"),
                PrimaryBranchId = ReadTopLevelStringAny(item, "primary_branch_id", "primary_branch"),
                BranchIds = ReadStringArray(item, "branch_ids"),
            };
            // 2026-10-28: поля сервера, которых нет в модели, сохраняем (round-trip: новые поля не теряются).
            foreach (var prop in item.EnumerateObject())
                if (!KnownEmployeeFields.Contains(prop.Name) && !prop.Name.StartsWith("can_", StringComparison.Ordinal))
                    dto.Extra[prop.Name] = prop.Value.Clone();
            list.Add(dto);
        }

        return list;
    }

    private static readonly HashSet<string> KnownEmployeeFields = new(StringComparer.Ordinal)
    {
        "id", "user_id", "uuid", "first_name", "firstName", "last_name", "lastName", "full_name", "fullName", "name", "fio",
        "display_name", "email", "phone", "phone_number", "role_display", "role_name", "role", "position", "user_role",
        "custom_role", "role_id", "password", "generated_password", "temp_password", "default_password", "track_number",
        "avatar", "primary_branch_id", "primary_branch", "branch_ids",
    };

    private static List<string>? ReadStringArray(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return null;
        return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
    }

    /// <summary>2026-10-28: параметры запроса следующей страницы из поля next (ссылка) или null, если страниц больше нет.</summary>
    private static Dictionary<string, string>? NextPageQuery(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("next", out var nx) || nx.ValueKind != JsonValueKind.String)
            return null;
        var link = nx.GetString() ?? "";
        var qi = link.IndexOf('?');
        if (qi < 0)
            return null;
        var result = new Dictionary<string, string>();
        foreach (var part in link[(qi + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            result[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
        }
        return result.Count == 0 ? null : result;
    }

    private static string MaskIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "not-set";
        var visibleLength = Math.Min(4, value.Length);
        return value[..visibleLength] + "****";
    }

    internal static CompanyDto? ParseCompanyDto(JsonElement root)
    {
        var data = root;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out var inner)
            && inner.ValueKind == JsonValueKind.Object)
        {
            data = inner;
        }

        if (data.ValueKind != JsonValueKind.Object)
            return null;

        string? planName = null;
        string? planPrice = null;
        if (data.TryGetProperty("subscription_plan", out var planEl) && planEl.ValueKind == JsonValueKind.Object)
        {
            planName = ReadTopLevelString(planEl, "name");
            planPrice = ReadTopLevelString(planEl, "price");
        }

        return new CompanyDto
        {
            Id = ReadTopLevelString(data, "id"),
            Name = ReadTopLevelString(data, "name"),
            Inn = TrimToMaxLength(ReadTopLevelString(data, "inn"), 32),
            Address = ReadTopLevelString(data, "address"),
            StartDate = ReadTopLevelString(data, "start_date"),
            EndDate = ReadTopLevelString(data, "end_date"),
            SubscriptionPlanName = planName,
            SubscriptionPlanPrice = planPrice,
            CanViewDocuments = ReadTopLevelBool(data, "can_view_documents"),
            CanViewWhatsapp = ReadTopLevelBool(data, "can_view_whatsapp"),
            CanViewInstagram = ReadTopLevelBool(data, "can_view_instagram"),
            CanViewTelegram = ReadTopLevelBool(data, "can_view_telegram"),
            CanViewShowcase = ReadTopLevelBool(data, "can_view_showcase"),
            CashierPassword = ReadTopLevelString(data, "cashier_password"),
            // 2026-09-08: "Код на удаление из корзины" и "Максимальная скидка" — реальные поля,
            // видны на app.nurcrm.kg (Моя компания → Касса), но их точное имя в JSON неизвестно
            // (нет доступа к серверному коду/документации) — пробуем несколько правдоподобных
            // вариантов подряд, как уже делает PermissionService.ExtractPermissionNames для похожей
            // проблемы с правами. Если ни один вариант не совпадёт с реальным именем поля —
            // значение останется null и гейт просто не активируется (см. EmployeeAccessGate/
            // MaxDiscountGate), ничего не сломается, только фича не подключится сама.
            CartDeleteCode = ReadTopLevelStringAny(data,
                "cart_delete_code", "delete_code", "cashier_delete_code", "basket_delete_code", "remove_item_code"),
            MaxDiscountPercent = ReadTopLevelDecimalAny(data,
                "max_discount_percent", "max_discount", "discount_limit_percent", "discount_limit", "max_discount_percentage"),
        };
    }

    private static string? ReadTopLevelString(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string? ReadTopLevelStringAny(JsonElement obj, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            var value = ReadTopLevelString(obj, name);
            if (value != null)
                return value;
        }
        return null;
    }

    /// <summary>Числовое поле, которое сервер мог отдать и как JSON-число, и как строку
    /// (типично для Decimal-полей в Django REST serializers, см. SubscriptionPlanPrice выше,
    /// отданное строкой) — пробует оба варианта для каждого имени по очереди.</summary>
    private static decimal? ReadTopLevelDecimalAny(JsonElement obj, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (!obj.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var num))
                return num;
            if (value.ValueKind == JsonValueKind.String
                && decimal.TryParse(value.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return null;
    }

    private static bool ReadTopLevelBool(JsonElement obj, string propertyName) =>
        obj.TryGetProperty(propertyName, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        && value.GetBoolean();

    /// <summary>Доступы сотрудника (can_view_*) — подтверждено живым GET api/users/employees/
    /// (2026-09-21), см. doc-comment у EmployeeAccessFlags. Поля, которых нет в JSON (компания
    /// не покупала эту доп. услугу), ReadTopLevelBool честно читает как false.</summary>
    private static bool? ReadOptionalBool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static EmployeeAccessFlags ReadEmployeeAccessFlags(JsonElement item) => new()
    {
        // 2026-10-28: поля схемы сервера; null — сервер не вернул (feature-detect, см. EmployeeAccessFlags).
        CanViewSalary = ReadOptionalBool(item, "can_view_salary"),
        CanEditSalary = ReadOptionalBool(item, "can_edit_salary"),
        CanEditEmployees = ReadOptionalBool(item, "can_edit_employees"),
        CanEditProducts = ReadOptionalBool(item, "can_edit_products"),
        CanEditClients = ReadOptionalBool(item, "can_edit_clients"),
        CanEditCashbox = ReadOptionalBool(item, "can_edit_cashbox"),
        CanEditSettings = ReadOptionalBool(item, "can_edit_settings"),
        CanEditProcurement = ReadOptionalBool(item, "can_edit_procurement"),
        CanEditDocuments = ReadOptionalBool(item, "can_edit_documents"),
        CanViewDebts = ReadOptionalBool(item, "can_view_debts"),
        CanEditPurchaseDocuments = ReadOptionalBool(item, "can_edit_purchase_documents"),
        CanUsePos = ReadOptionalBool(item, "can_use_pos"),
        CanViewCashbox = ReadTopLevelBool(item, "can_view_cashbox"),
        CanViewAnalytics = ReadTopLevelBool(item, "can_view_analytics"),
        CanViewProducts = ReadTopLevelBool(item, "can_view_products"),
        CanViewSale = ReadTopLevelBool(item, "can_view_sale"),
        CanViewClients = ReadTopLevelBool(item, "can_view_clients"),
        CanViewBrandCategory = ReadTopLevelBool(item, "can_view_brand_category"),
        CanViewEmployees = ReadTopLevelBool(item, "can_view_employees"),
        CanViewSettings = ReadTopLevelBool(item, "can_view_settings"),
        CanViewMarketProcurement = ReadTopLevelBool(item, "can_view_market_procurement"),
        CanViewMarketSupplier = ReadTopLevelBool(item, "can_view_market_supplier"),
        CanViewCashier = ReadTopLevelBool(item, "can_view_cashier"),
        CanViewShifts = ReadTopLevelBool(item, "can_view_shifts"),
        CanViewDocument = ReadTopLevelBool(item, "can_view_document"),
        CanViewMarketDiscount = ReadTopLevelBool(item, "can_view_market_discount"),
        CanViewMarketEditPrice = ReadTopLevelBool(item, "can_view_market_edit_price"),
        CanViewMarketDeleteCartItem = ReadTopLevelBool(item, "can_view_market_delete_cart_item"),
        CanViewMarketEmployeeReturn = ReadTopLevelBool(item, "can_view_market_employee_return"),
        CanViewMarketLabel = ReadTopLevelBool(item, "can_view_market_label"),
        CanViewMarketScales = ReadTopLevelBool(item, "can_view_market_scales"),
        CanViewWhatsapp = ReadTopLevelBool(item, "can_view_whatsapp"),
        CanViewTelegram = ReadTopLevelBool(item, "can_view_telegram"),
        CanViewInstagram = ReadTopLevelBool(item, "can_view_instagram"),
        CanViewDocuments = ReadTopLevelBool(item, "can_view_documents"),
    };

    private static string? TrimToMaxLength(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        return value.Length > maxLength ? value[..maxLength] : value;
    }

    /// <summary>
    /// Универсальный запрос с Bearer и query branch=… (как branch_params() в Python).
    /// </summary>
    /// <param name="requestTimeout">Ограничение времени запроса (например scan 22 с).</param>
    /// 
}
