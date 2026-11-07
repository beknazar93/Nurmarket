using Avalonia.Headless;
using NurMarketKassa.UiTests.Harness;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]
// 2026-11-06 (В14): окна, хост служб, общий поддельный сервер и статика кассы — одни на весь прогон; тесты идут строго по очереди.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
