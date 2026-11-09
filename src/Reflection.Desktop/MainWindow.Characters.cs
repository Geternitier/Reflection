using Reflection.Core;

namespace Reflection.Desktop;

public partial class MainWindow
{
    private TaskCompletionSource? characterSaveCompleted;
    private CancellationTokenSource? characterSaveCancellation;
    internal bool IsOverlayContent(object content) => ReferenceEquals(OverlayContent.Child, content);

    private void BeginCharacterSave()
    {
        savingCharacter = true;
        characterSaveCancellation = new();
        characterSaveCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void EndCharacterSave()
    {
        savingCharacter = false;
        characterSaveCompleted?.TrySetResult();
        characterSaveCompleted = null;
        characterSaveCancellation?.Dispose(); characterSaveCancellation = null;
    }

    // Publish changes only after the library file commits successfully.
    private async Task CommitCharacterLibraryAsync(CharacterCard? original, CharacterCard? replacement)
    {
        var index = original is null ? -1 : VM.Characters.IndexOf(original);
        if (original is not null && index < 0) throw new InvalidOperationException("该角色已被删除。");
        var updated = VM.Characters.ToList();
        if (index < 0)
        {
            if (replacement is null) throw new ArgumentException("缺少角色。");
            updated.Add(replacement);
        }
        else if (replacement is null) updated.RemoveAt(index);
        else updated[index] = replacement;

        await VM.Store.SaveCharactersAsync(updated);
        if (index < 0) VM.Characters.Add(replacement!);
        else if (replacement is not null) VM.Characters[index] = replacement;
        else
        {
            var snapshot = VM.Current.CharacterSnapshot;
            var characterId = VM.Current.CharacterId;
            VM.Characters.RemoveAt(index);
            if (VM.SelectedCharacter == original) VM.SelectedCharacter = null;
            VM.DetailCard = VM.Characters.FirstOrDefault();
            VM.RestoreCharacter(snapshot, characterId);
        }
    }
}
