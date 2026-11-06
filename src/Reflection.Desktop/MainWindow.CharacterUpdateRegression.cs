using Reflection.Core;

namespace Reflection.Desktop;

public partial class MainWindow
{
    internal async Task VerifyCharacterUpdatesAsync()
    {
        var card = new CharacterCard { Name = "更新验证角色", SystemPrompt = "old rules", Setting = "old setting", Greeting = "original opening" };
        VM.Characters.Add(card); await VM.SaveCharactersAsync();
        var other = VM.Characters.First(c => c.Id != card.Id);
        await VM.NewChatAsync(card);
        VM.Current.Messages.Add(new() { Content = "old question" }); await VM.SaveCurrentAsync();
        var firstId = VM.Current.Id;
        await VM.NewChatAsync(card);
        VM.Current.Messages.Add(new() { Content = "background question" }); await VM.SaveCurrentAsync();
        var second = VM.Current; var stamp = second.UpdatedAt;
        Task? background = null;
        if (VM.SelectedModel is not null)
        {
            VM.Draft = "navigation slow character update"; background = VM.SendAsync();
            await WaitOverlayStateAsync(() => VM.Busy);
        }
        await VM.NewChatAsync(other);
        var unrelated = VM.Current; var unrelatedSnapshot = unrelated.CharacterSnapshot;
        var order = VM.Conversations.Select(c => c.Id).ToArray();
        BeginCharacterEdit(card);
        CharacterEditor.NameField.Text = "updated character";
        CharacterEditor.DescriptionField.Text = "updated description";
        CharacterEditor.SettingField.Text = "updated setting";
        CharacterEditor.SystemField.Text = "updated rules";
        CharacterEditor.GreetingField.Text = "replacement opening";
        if (!await SaveCharacterAsync()) throw new InvalidOperationException("Character update could not save");
        if (!ReferenceEquals(VM.Current, unrelated) || !ReferenceEquals(unrelated.CharacterSnapshot, unrelatedSnapshot)) throw new InvalidOperationException("Character update changed unrelated active chat");
        var edited = VM.DetailCard!;
        foreach (var id in new[] { firstId, second.Id })
        {
            var saved = await VM.Store.LoadConversationAsync(id);
            if (saved.CharacterSnapshot is not { Name: "updated character", Description: "updated description", Setting: "updated setting", SystemPrompt: "updated rules", Greeting: "original opening" } || saved.Messages[0].Content != "original opening" || !saved.Messages[0].Opening)
                throw new InvalidOperationException("Character settings did not update persisted linked history independently of opening");
            if (VM.Conversations.First(c => c.Id == id).DisplayTitle != "updated character") throw new InvalidOperationException("Updated character name did not reach history title");
        }
        if (!order.SequenceEqual(VM.Conversations.Select(c => c.Id))) throw new InvalidOperationException("Character edit reordered conversations");
        if (background is not null)
        {
            if (background.IsCompleted || second.CharacterSnapshot?.SystemPrompt != "updated rules") throw new InvalidOperationException("Character edit stopped or failed to update the background conversation");
            await background;
            if (second.Messages.Last().Stopped || second.Messages.Last().HasError) throw new InvalidOperationException("Character edit interrupted background output");
        }
        else if (second.UpdatedAt != stamp) throw new InvalidOperationException("Character update changed conversation activity time");
        await VM.OpenAsync(VM.Conversations.First(c => c.Id == firstId));
        if (VM.Current.CharacterSnapshot?.SystemPrompt != "updated rules" || VM.Current.Messages[0].Content != "original opening") throw new InvalidOperationException("Opening history reverted updated role settings");
        if (VM.SelectedModel is not null)
        {
            VM.Draft = "character update check"; await VM.SendAsync();
            if (VM.Current.Messages.Last().HasError) throw new InvalidOperationException("Updated character request failed");
        }
        await VM.NewChatAsync(edited);
        if (VM.Current.Messages[0].Content != "replacement opening") throw new InvalidOperationException("New conversation did not use edited opening");
        VM.Characters.Remove(edited); await VM.SaveCharactersAsync();
        await VM.NewChatAsync(other);
    }
}
