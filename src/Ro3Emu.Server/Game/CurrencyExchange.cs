using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The currency exchange window (ItemExchangeReq 2005, <c>UI_Sys_CoinExchangeWidgetCtrl._itemConvert</c>): the client sends the
/// paid currency's item id and how many to pay; the row of <c>ItemExchangeConfig</c> keyed by that id names the target currency
/// (<c>_iTargetID</c>) and how many of it one paid unit gives (<c>_iRate</c>, 100 in both exported rows). The payment and the grant
/// share one transaction (<see cref="CharacterService.ExchangeAsync"/>); a short balance changes nothing.
/// </summary>
public sealed class CurrencyExchange(GameData data, CharacterService character, FunctionService functions)
{
    /// <returns>0 on success, otherwise the client error code.</returns>
    public async Task<int> ExchangeAsync(GameSession session, long itemId, long count)
    {
        if (session.Role == null || session.Progress == null) return ErrorCodes.Parameter;
        if (data.Row("ItemExchangeConfig", itemId) is not { } row) return ErrorCodes.Parameter;
        long target = GameData.Int(row, "_iTargetID"), rate = GameData.Int(row, "_iRate"), limit = GameData.Int(row, "_iSingleLimit");
        if (target <= 0 || rate <= 0 || count < 1 || count > limit || count * rate > int.MaxValue) return ErrorCodes.Parameter;
        // The window's own open condition (base level 10 for 7006, 20 for 7005 in the export).
        long function = GameData.Int(row, "_iFunction");
        if (function != 0 && !functions.IsOpen(session, function)) return ErrorCodes.FeatureUnavailable;

        var cost = new[] { ((int)itemId, count) };
        if (await character.ExchangeAsync(session, cost, [((int)target, (int)(count * rate))]) != null) return 0;
        return character.ShortageOf(session, cost);
    }
}
