using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>2026-11-01 (ответ Умара по ТЗ 01.11): признаки витрины в GET api/main/capabilities/ (тот же список `features`, что у
/// receipt_delete и receipt_edit): showcase_telegram (кнопки Telegram на сайте), showcase_cart_link (перенос корзины в бот),
/// showcase_order_source_detail (source_detail у заказов и фильтры ?source=, ?source_detail=), showcase_chat (чат на сайте),
/// showcase_assistant (ИИ-помощник магазина). Ответ держим 10 минут; ошибка запроса = «нет признаков» (на 1 минуту) —
/// редактор сайта и «Заказы с сайта» тогда работают как раньше, а новые блоки пишут «Скоро: ждём сервер».</summary>
public static class ShowcaseCapabilities
{
    public const string Telegram = "showcase_telegram";
    public const string CartLink = "showcase_cart_link";
    public const string OrderSourceDetail = "showcase_order_source_detail";
    public const string Chat = "showcase_chat";
    public const string Assistant = "showcase_assistant";

    // 2026-11-01 (контрольное ревью): запрос, разбор и кэш — в одном классе ServerCapabilities (его же использует SupplierApiService); здесь только имена признаков витрины.
    public static bool? Cached(string name) => ServerCapabilities.Cached(name);

    public static Task<bool> HasAsync(NurMarketApiClient api, string name, CancellationToken ct = default) => ServerCapabilities.HasAsync(api, name, ct);

    public static Task<(IReadOnlySet<string> Features, Exception? Error)> TryLoadAsync(NurMarketApiClient api, CancellationToken ct = default, bool force = false) => ServerCapabilities.TryLoadAsync(api, ct, force);

    public static Task<IReadOnlySet<string>> LoadAsync(NurMarketApiClient api, CancellationToken ct = default, bool force = false) => ServerCapabilities.LoadAsync(api, ct, force);
}
