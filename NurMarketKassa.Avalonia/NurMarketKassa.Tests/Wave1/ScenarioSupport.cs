using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Infrastructure;
using WireMock.Logging;

namespace NurMarketKassa.Tests.Wave1;

/// <summary>2026-11-06: В1 — общая обвязка сквозных тестов служб (WireMock): заглушки-«пустышки» для зависимостей оплаты, корзина-заглушка,
/// вход настоящего клиента API в поддельный NurCRM и разбор записанных запросов. Только для тестов; код приложения не затрагивается.</summary>
internal static class ScenarioSupport
{
    /// <summary>Любой метод интерфейса возвращает «ничего»: Task — завершённая, Task&lt;T&gt; — с default(T), значения — default.</summary>
    public class NullProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => DefaultFor(targetMethod);

        internal static object? DefaultFor(MethodInfo? m)
        {
            var type = m?.ReturnType;
            if (type is null || type == typeof(void))
                return null;
            if (type == typeof(Task))
                return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, new[] { value });
            }
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }

    /// <summary>Корзина-заглушка: чек из JSON; сброс чека после оплаты и прочее — ничего не делает.</summary>
    public class CartProxy : DispatchProxy
    {
        public string Json = "{}";
        public int ResetCalls;
        private JsonElement _root;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_HasCart": return true;
                case "get_LineCount": return CartDisplayHelper.EnumerateItems(Root()).Count();
                case "GetRawText": return Json;
                case "get_Root": return Root();
                case "ResetForNewReceipt": ResetCalls++; return null;
                default: return NullProxy.DefaultFor(targetMethod);
            }
        }

        private JsonElement Root()
        {
            if (_root.ValueKind == JsonValueKind.Undefined)
                _root = JsonDocument.Parse(Json).RootElement.Clone();
            return _root;
        }
    }

    public static T Null<T>() where T : class => DispatchProxy.Create<T, NullProxy>();

    public static ICartService MakeCart(string json)
    {
        var cart = DispatchProxy.Create<ICartService, CartProxy>();
        ((CartProxy)(object)cart).Json = json;
        return cart;
    }

    /// <summary>Настоящий клиент API, вошедший в поддельный сервер.</summary>
    public static async Task<NurMarketApiClient> LoggedInAsync(FakeNurCrmServer fake)
    {
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = fake.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        return api;
    }

    /// <summary>Чек для тестов: позиции — (название, количество, цена, скидка строки); id товаров — настоящие UUID, иначе быстрая оплата чек не берёт.</summary>
    public static string CartJson(IEnumerable<(string Name, double Qty, double Price, double Discount)> lines, object? orderDiscountPercent = null)
    {
        var items = new JsonArray();
        foreach (var l in lines)
        {
            var item = new JsonObject
            {
                ["product_id"] = Guid.NewGuid().ToString(),
                ["server_is_weight"] = false,
                ["product_name"] = l.Name,
                ["quantity"] = l.Qty,
                ["unit_price"] = l.Price,
                ["line_total"] = l.Qty * l.Price,
            };
            if (l.Discount > 0)
                item["discount_total"] = l.Discount;
            items.Add(item);
        }
        var root = new JsonObject { ["items"] = items };
        if (orderDiscountPercent is not null)
            root["order_discount_percent"] = JsonValue.Create(orderDiscountPercent);
        return root.ToJsonString();
    }

    /// <summary>Запросы, дошедшие до поддельного сервера, по пути и методу.</summary>
    public static IReadOnlyList<ILogEntry> Requests(FakeNurCrmServer fake, string method, string pathPart) =>
        fake.Server.LogEntries
            .Where(e => string.Equals(e.RequestMessage.Method, method, StringComparison.OrdinalIgnoreCase)
                        && e.RequestMessage.Path.Contains(pathPart, StringComparison.OrdinalIgnoreCase))
            .ToList();

    public static JsonObject BodyOf(ILogEntry entry) => (JsonObject)JsonNode.Parse(entry.RequestMessage.Body!)!;

    public static string? Header(ILogEntry entry, string name) =>
        entry.RequestMessage.Headers is { } h && h.TryGetValue(name, out var v) ? v.FirstOrDefault() : null;
}
