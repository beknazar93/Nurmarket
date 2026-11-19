using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-19 (склад «10 из 10», К-4): блок «Партии» в карточке чека владельца — из <c>allocations</c> строк продажи (партия, количество, себестоимость, срок) и «Себестоимость по партиям».
/// Блок появляется ТОЛЬКО если сервер отдал allocations (партии включены у компании); у старых компаний и чеков до включения партий в карточке ничего не меняется.
/// Себестоимость видна только ролям с правом видеть закупку.</summary>
public static class SaleBatchesCard
{
    public const string PopupTag = "sale-batches";

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Вставить блок в панель карточки (старый такой же блок убирается). Нет allocations — ничего не вставляется.</summary>
    public static void Place(Panel? host, int index, JsonElement sale, bool showCost)
    {
        if (host is null)
            return;
        foreach (var old in host.Children.Where(c => Equals(c.Tag, PopupTag)).ToList())
            host.Children.Remove(old);
        var block = Build(sale, showCost);
        if (block is null)
            return;
        host.Children.Insert(Math.Min(Math.Max(index, 0), host.Children.Count), block);
    }

    /// <summary>Сам блок (для проверки без окна). null — allocations нет ни у одной строки.</summary>
    public static Control? Build(JsonElement sale, bool showCost)
    {
        var rows = new List<(string Name, SaleAllocation A)>();
        double? total = null;
        foreach (var line in CartDisplayHelper.EnumerateSaleLineItems(sale))
        {
            var name = CartDisplayHelper.ItemName(line).Trim();
            var allocations = ServerStock.ParseAllocations(line);
            foreach (var a in allocations)
                rows.Add((name, a));
            if (ServerStock.AllocationsCost(allocations) is { } c)
                total = (total ?? 0) + c;
        }
        if (rows.Count == 0)
            return null;

        var panel = new StackPanel { Spacing = 4 };
        var title = new TextBlock { Text = T("Партии", "Партиялар", "Batches", "Partiler", "Partiyalar"), FontSize = 14, FontWeight = FontWeight.SemiBold };
        title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("BrushText"));
        panel.Children.Add(title);
        foreach (var (name, a) in rows)
        {
            var parts = new List<string>
            {
                a.LotNo.Length > 0 ? T($"партия {a.LotNo}", $"партия {a.LotNo}", $"batch {a.LotNo}", $"parti {a.LotNo}", $"partiya {a.LotNo}") : T("партия без номера", "номерсиз партия", "batch without a number", "numarasız parti", "raqamsiz partiya"),
                ServerStock.Q(a.Qty) + " " + T("шт", "даана", "pcs", "adet", "dona"),
            };
            if (showCost && a.UnitCost is { } cost)
                parts.Add(T("себестоимость ", "өздүк баа ", "cost ", "maliyet ", "tannarx ") + MoneyText.Som(cost));
            if (a.ExpiresAt is { } e)
                parts.Add(T($"срок {e:dd.MM.yyyy}", $"мөөнөтү {e:dd.MM.yyyy}", $"expires {e:dd.MM.yyyy}", $"SKT {e:dd.MM.yyyy}", $"muddati {e:dd.MM.yyyy}"));
            var t = new TextBlock { Text = name + ": " + string.Join(" · ", parts), FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
            panel.Children.Add(t);
        }
        if (showCost && total is { } sum)
        {
            var t = new TextBlock { Text = T("Себестоимость по партиям: ", "Партиялар боюнча өздүк баа: ", "Cost by batches: ", "Partilere göre maliyet: ", "Partiyalar bo'yicha tannarx: ") + MoneyText.Som(sum), FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushText"));
            panel.Children.Add(t);
        }
        var border = new Border { Tag = PopupTag, Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 8, 0, 0), Child = panel };
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("BrushBorder"));
        return border;
    }
}
