# Player trade, character-to-character send and account vault

Not original behaviour: the original client has no direct trade window, no way to send items to another character and no
shared storage between characters. This fork adds them with its own client window (`Ro3Emu.ClientHooks/TradeWindow.cs`)
and its own game messages. Everything below is **(inferred / fork-specific)**.

## Wire protocol

Game messages inside the already allowed 77000..77099 range, bodies are UTF-8 JSON objects (no protobuf).

| Message | Direction | Constant |
| --- | --- | --- |
| 77020 | client -> server | `PlayerTradeService.UpMsg` / `TradeWindow.UpMsgId` |
| 77021 | server -> client | `PlayerTradeService.DownMsg` / `TradeWindow.DownMsgId` |

Requests carry `"op"`. Replies/pushes carry `"t"`. Unknown keys are ignored. Every request may be answered by one or more pushes.

### Item descriptor `I`

```json
{"guid": 1000123, "entry": 2100560, "name": "Wizardry Staff", "stack": 1, "q": 6, "lv": 9}
```

`guid` (bag instance) is the identity used in requests; `name` is the server's item name (the client may replace it with its
own localized name); `q` is the ItemConfig quality (1..6, used to colour the name); `lv` (optional) is the refine level.

### Client -> server (`op`)

| op | other fields | meaning |
| --- | --- | --- |
| `open` | `tab` (optional `trade`/`send`/`vault`) | ask for the full state; the server answers `bag`, `trade`, `vault` |
| `bag` | | refresh the tradable bag |
| `trade.invite` | `name` | invite an online player (character name) |
| `trade.accept` / `trade.decline` | | answer the pending invitation |
| `trade.offer` | `items` `[{"guid":n,"n":count}]`, `zeny` | replace MY offer wholesale (idempotent). Clears both `ready` flags |
| `trade.ready` | `on` (bool) | I agree to the current offers. When both are ready the server commits |
| `trade.cancel` | | leave the trade |
| `send` | `name`, `items`, `zeny` | send items / zeny to any character (online or offline) by mail |
| `vault.put` | `items`, `zeny` | move bag items / zeny into the account vault |
| `vault.take` | `items` `[{"id":vaultId,"n":count}]`, `zeny` | move vault items / zeny into the mailbox of the current character |

### Server -> client (`t`)

| t | fields |
| --- | --- |
| `show` | `tab`: open (or switch) the window on this tab |
| `bag` | `items`: tradable bag items `[I]`, `zeny`: current zeny |
| `trade` | `state`: `none` / `invited` / `open`. `invited`: `peer`, `incoming` (true = I was invited, false = I invited). `open`: `peer`, `mine` and `theirs` = `{"items":[I],"zeny":n,"ready":bool}`, `readyAfterMs` (milliseconds until `trade.ready` is accepted after the last change; the client greys the button meanwhile) |
| `vault` | `items`: `[{"id":n,"entry":..,"name":..,"stack":..,"q":..,"from":"CharacterName"}]`, `zeny`, `capacity`, `used` |
| `msg` | `text` (already Portuguese), `ok` (bool). Shown in the window's status line |
| `done` | `what`: `trade`/`send`/`vault`: an operation finished (the client clears its selection) |

The server is authoritative: the client only displays and sends requests. A refused request answers `msg` with `ok=false`.

## Rules (server)

* Eligible items are the ones the trading house accepts (`TradeService.TransactionItemError`: not bound, not locked, not
  equipped, not in escrow, etc.). Zeny is currency 1.
* Trade: both players online, same scene and line, neither dead nor in an instance/dungeon. At most 10 item rows per side.
  Changing an offer (either side) clears both `ready` flags; `ready` is ignored for 2 seconds after the last change.
  Invitation expires after 60 s; the trade closes after 5 minutes without progress, on logout, on disconnect or on scene change.
* Settlement is one `MarketStore` transaction: items and zeny leave both bags, each side receives the other's goods as
  **system mail** (a letter with attachments, claimed from the mailbox like stall purchases). Refused as a whole when either
  bag changed meanwhile.
* Send: same mechanism, one direction, to any existing character by name; the recipient gets a letter naming the sender.
* Vault: one per account (shared by all its characters), 60 slots, zeny unlimited. Deposit is immediate; a withdrawal is
  delivered as mail to the withdrawing character (bag capacity is checked at claim time like every mail).
* Every committed trade/send/vault move is appended to `<data>/player-trade-log.jsonl`.

## Chat commands (players)

`/troca [name]` opens the window (and invites `name` if given), `/enviar`, `/cofre` open the window on the other tabs.
