using System.IO;
using System.Windows;
using System.Windows.Controls;
using Reflection.Core;

namespace Reflection.Desktop;

public partial class MainWindow
{
    internal async Task VerifyCharacterSaveFailuresAsync(string output)
    {
        var card = VM.Characters.First();
        await VM.NewChatAsync(card);
        VM.DetailCard = card; ShowPage("detail");
        var snapshot = VM.Current.CharacterSnapshot;
        var ids = VM.Characters.Select(c => c.Id).ToArray();
        var library = Path.Combine(VM.Store.Root, "characters.json");
        var before = await File.ReadAllTextAsync(library);
        var imported = Path.Combine(output, "save-failure-card.json");
        await File.WriteAllTextAsync(imported, CharacterExchange.Export(new() { Name = "保存失败回归角色" }));
        try
        {
            using (var locked = new FileStream(library, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                FileDialogs.SelectForRegression = _ => [imported];
                ImportCard_Click(this, new RoutedEventArgs());
                if (characterSaveCompleted is { } pending) await pending.Task;
                CheckUnchanged();
                if (!VM.Status.StartsWith("操作未完成：")) throw new InvalidOperationException("Import write failure was not reported");

                DeleteDetail_Click(this, new RoutedEventArgs());
                await WaitOverlayStateAsync(() => OverlayOpen);
                ((InlineNotice)OverlayContent.Child).PrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitOverlayStateAsync(() => !OverlayOpen);
                if (characterSaveCompleted is { } deleting) await deleting.Task;
                CheckUnchanged();
                if (!VM.Status.StartsWith("操作未完成：")) throw new InvalidOperationException("Delete write failure was not reported");

                BeginCharacterEdit(card); CharacterEditor.NameField.Text = "失败后保留的草稿";
                if (await SaveCharacterAsync() || CharacterEditor.NameField.Text != "失败后保留的草稿" || !CharacterEditor.ErrorText.Text.StartsWith("保存失败："))
                    throw new InvalidOperationException("Edit write failure lost its draft or reported success");
                CheckUnchanged();
                CancelCharacter_Requested(this, EventArgs.Empty);
            }
            if (await File.ReadAllTextAsync(library) != before) throw new InvalidOperationException("Failed character operation changed disk data");
            ImportCard_Click(this, new RoutedEventArgs());
            if (characterSaveCompleted is { } retry) await retry.Task;
            var added = VM.DetailCard!;
            if (added.Name != "保存失败回归角色" || VM.Characters.Count != ids.Length + 1 || !(await VM.Store.LoadCharactersAsync()).Any(c => c.Id == added.Id))
                throw new InvalidOperationException("Import retry failed or duplicated the character");
            await CommitCharacterLibraryAsync(added, null);
            if ((await VM.Store.LoadCharactersAsync()).Any(c => c.Id == added.Id) || VM.Characters.Contains(added) || !ReferenceEquals(VM.Current.CharacterSnapshot, snapshot))
                throw new InvalidOperationException("Successful deletion did not preserve the conversation snapshot");

            // A committed card must not be reported as unsaved if a linked chat cannot be read.
            await VM.SaveCurrentAsync();
            var chatPath = Path.Combine(VM.Store.Root, "chats", VM.Current.Id + ".json");
            using (var lockedChat = new FileStream(chatPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                BeginCharacterEdit(card); CharacterEditor.NameField.Text = "已提交但同步失败";
                if (!await SaveCharacterAsync() || currentPage != "detail" || !VM.Status.StartsWith("角色已保存，但部分会话同步失败：") || VM.DetailCard?.Name != "已提交但同步失败")
                    throw new InvalidOperationException("Partial sync failure misreported the committed card");
            }
            var committed = VM.DetailCard!;
            if ((await VM.Store.LoadCharactersAsync()).First(c => c.Id == card.Id).Name != committed.Name)
                throw new InvalidOperationException("Partial failure did not persist the library");
            BeginCharacterEdit(committed);
            if (!await SaveCharacterAsync() || VM.Status != "角色已保存") throw new InvalidOperationException("Character synchronization could not retry");
        }
        finally { FileDialogs.SelectForRegression = null; }

        void CheckUnchanged()
        {
            if (!ids.SequenceEqual(VM.Characters.Select(c => c.Id)) || VM.DetailCard != card || VM.SelectedCharacter != card || !ReferenceEquals(VM.Current.CharacterSnapshot, snapshot))
                throw new InvalidOperationException("Failed library write changed visible character state");
        }
    }
}
