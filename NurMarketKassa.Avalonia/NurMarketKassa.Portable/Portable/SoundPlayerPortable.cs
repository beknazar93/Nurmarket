namespace System.Media;

// 2026-10-04: System.Media.SoundPlayer есть только в Windows. В переносимой сборке звуки кассы
// (сигнал о сроке проката, голосовые подсказки) отдаются платформе через PortablePlatform.PlayWav:
// Linux — aplay/paplay, Android — свой проигрыватель. Если платформа звук не умеет — тишина, без ошибки.
public sealed class SoundPlayer : IDisposable
{
    private readonly Stream _stream;

    public SoundPlayer(Stream stream) => _stream = stream;

    public void PlaySync()
    {
        try
        {
            if (_stream.CanSeek)
                _stream.Position = 0;
            using var copy = new MemoryStream();
            _stream.CopyTo(copy);
            NurMarketKassa.AvaloniaHost.Portable.PortablePlatform.PlayWav(copy.ToArray());
        }
        catch (Exception ex)
        {
            NurMarketKassa.Services.PosLogger.Log($"Звук не воспроизведён: {ex.Message}", "WARNING");
        }
    }

    public void Play() => _ = Task.Run(PlaySync);

    public void Dispose() { }
}
