using System.Windows;

namespace Reflection.Desktop;

public partial class MainWindow
{
    private async void ContextButton_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (!VM.Idle) return;
        var owner = VM;
        var chat = owner.Current;
        var (accepted, value) = await EditorDialog.ContextWindowAsync(this, chat.ContextWindowOverride, owner.ContextUsageHint);
        if (accepted && ReferenceEquals(VM, owner) && ReferenceEquals(owner.Current, chat) && owner.Idle)
            await owner.SetContextWindowOverrideAsync(value);
    });
}
