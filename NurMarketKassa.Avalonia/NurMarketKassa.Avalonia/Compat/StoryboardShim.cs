namespace Avalonia.Animation;

public sealed class Storyboard
{
    public void Begin() { }
    public void Stop() { }

#pragma warning disable CS0067 // Reserved for WPF-ported animation call sites
    public event EventHandler? Completed;
#pragma warning restore CS0067
}
