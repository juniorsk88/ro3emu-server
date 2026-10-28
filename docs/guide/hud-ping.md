# The HUD ping (the "ms" next to the FPS)

The client's network indicator showed **0 ms** on this server. It was not a network problem: the server did not echo a tag
the client needs.

## How the client measures it

The publisher's `RO3.PingTiming.dll` (`RO3.ClientTiming.HeartbeatCore`) times the heartbeat (message id 1):

1. Before a `Ping` is sent, `Prepare` puts a tag in `Ping.strDebugTag` (field 1): `ro3pt:` + a counter in 16 hex digits, followed
   by `|` and the game's own debug tag when there is one.
2. When the reply arrives, `Match` reads `Pong.strDebugTag` (field 2) and compares it **byte for byte** with the tags it is
   waiting for. Only a match produces a time: the round trip in milliseconds minus the client's own queue wait.
3. A `Pong` without the tag matches nothing, so the HUD stays at 0 ms.

`LC_PingHelper` (`UnityEngine.Ping`, ICMP) is a different thing: it only times the servers in the login server list.

## What the server does

`CoreMessages.HeartbeatTag` returns the `Ping`'s `strDebugTag` and the heartbeat handler puts it in the `Pong`
(`Game/Messages/CoreMessages.cs`). An empty or malformed `Ping`, like the one the bots send, gets a `Pong` with no tag.

The `Pong` is still sent after the daily, dungeon and guild reconciliation, because the tests and bots use it as the sign
that everything sent before it was handled. Under heavy load the HUD value therefore also contains that processing time.
If a load test shows it matters, answering first is the change to consider.

## Check

- Test: `ReconnectTests.HeartbeatPongReturnsThePingTag` (a `Ping` with a tag gets the same tag back; an empty one still gets a `Pong`).
- In the game: the indicator next to the FPS shows a value above 0 ms (about 32 ms on the first check, 21 Oct 2026).
