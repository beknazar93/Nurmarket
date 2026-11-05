using Avalonia.Controls;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-05, просьба владельца: «переведи на кыргызский», «кыргызча жооп бер», «кыргызча сүйлө» — ИИ переходит на нужный язык и отвечает только на нём.
/// Язык ИИ (<see cref="UserPreferences.AiLanguageOverride"/>) НЕ угадывается по звуку и сам не уходит на узбекский/турецкий: он меняется настройкой программы или
/// явной командой пользователя (текст чата, голосовой вопрос, звонок). Распознаёт команды <see cref="AiLanguageCommand"/> (без модели).
/// «Переведи программу/интерфейс на …» меняет язык интерфейса (если открыт языковой пакет для en/tr/uz), остальное — только язык ИИ.</summary>
public sealed partial class AiAdvisorWindow
{
    private readonly TextBlock _liveLang = new() { FontSize = 11.5 };

    /// <summary>Подпись «Язык ИИ: Кыргызча» в окне звонка.</summary>
    private void AttachLiveLanguageLabel(StackPanel texts)
    {
        Use(_liveLang, TextBlock.ForegroundProperty, "BrushTextSoft");
        texts.Children.Add(_liveLang);
        RefreshLiveLanguage();
    }

    private void RefreshLiveLanguage() =>
        _liveLang.Text = T("Язык ИИ: ", "ЖИ тили: ", "AI language: ", "YZ dili: ", "SI tili: ") + AiVoiceLanguage.Label(AiVoiceLanguage.Current());

    /// <summary>Подписка звонка на команды смены языка из речи владельца.</summary>
    private void WireLiveLanguage(GeminiLiveVoice live)
    {
        live.AiLanguageChanged += _ => Dispatcher.UIThread.Post(() =>
        {
            if (_live == live)
                RefreshLiveLanguage();
        });
        live.InterfaceLanguageRequested += language => Dispatcher.UIThread.Post(() =>
        {
            if (_live == live)
                ApplyLanguageCommand(new AiLanguageCommand.Result(language, true));
        });
    }

    /// <summary>Команда в тексте чата / голосовом вопросе. true — это была команда (в ИИ не уходит).</summary>
    private bool TryHandleLanguageCommand(string question)
    {
        if (AiLanguageCommand.Detect(question) is not { } command)
            return false;
        ApplyLanguageCommand(command);
        return true;
    }

    private void ApplyLanguageCommand(AiLanguageCommand.Result command)
    {
        var current = AiVoiceLanguage.Current();
        if (!LanguagePackGate.IsAvailable(command.Language))
        {
            AddBubble(AiVoiceLanguage.PackMissingText(current), fromOwner: false);
            return;
        }

        if (command.Interface)
        {
            var prefs = UserPreferences.Instance;
            prefs.Language = command.Language;
            prefs.AiLanguageOverride = null; // ИИ снова следует интерфейсу
            prefs.SaveToDisk();
            LocalizationManager.Apply(command.Language);
            PosLogger.Log($"ИИ-советник: язык интерфейса сменён командой на {command.Language}.", "INFO");
        }
        else
        {
            AiVoiceLanguage.SetAiLanguage(command.Language);
            PosLogger.Log($"ИИ-советник: язык ИИ сменён командой на {command.Language}.", "INFO");
            if (_live is { IsOpen: true } live)
                _ = live.SendTextAsync(AiVoiceLanguage.SwitchText(command.Language));
        }

        RefreshLiveLanguage();
        AddBubble(AiVoiceLanguage.ConfirmText(command.Language), fromOwner: false);
    }
}
