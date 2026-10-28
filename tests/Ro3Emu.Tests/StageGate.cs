using Google.Protobuf;
using Ro3Emu.Bot;

namespace Ro3Emu.Tests;

/// <summary>
/// The stage gate (upstream #36): a game message that needs a role is dropped unanswered before the role is in the world,
/// so a test that used to read a defensive refusal from the handler now checks that no answer arrives.
/// </summary>
internal static class StageGate
{
    /// <summary>Sends <paramref name="message"/> and checks that no <paramref name="answer"/> message arrives for it.</summary>
    public static async Task Unanswered(BotClient bot, uint request, IMessage message, uint? answer = null)
    {
        uint id = answer ?? request;
        int before = bot.Inbox.Snapshot().Count(e => e.IsGame(id));
        await bot.SendGameAsync(request, message);
        await Task.Delay(600);
        Assert.Equal(before, bot.Inbox.Snapshot().Count(e => e.IsGame(id)));
    }
}
