# Client release 140075: the signed-origins guard

The publisher's release 140075 (21 Oct 2026, `ro3-live-na-na-fallback-v2-five-140074-2026.10.21.1`) added a "sealed login
fallback" to `Happyengine.Network.dll` (namespace `RO3.SealedLoginFallback`). Older releases do not have it.

## What it does

- `RegionalBootstrap` is a thin public facade (`RewriteUrl`, `NormalizeForGuard`, `NormalizeImageForGuard`, `WrapLogin`,
  `ConfigureLoginRequest`) over `SealedFallbackRuntime`. Its static constructor initialises the runtime with a compiled
  list of origins (`server.genesis3.is`, `update.genesis3.is`, `playgenesis3.is`, `playgns.is`, ...) and an RSA public
  key for a signed `origins.json`.
- The login (`HttpNetworkManager.RecoveryOfficialSdkLoginStart`) and many HTTP requests go through it: the URL is rewritten
  to an origin the publisher authorised, after a health probe. A server on another host is never used.
- A publisher bug: `RecoveryOfficialSdkLoginStart` takes the address (`ldftn`) of `SealedFallback_SelectionFailed`, a
  **private** method of its nested coroutine class `d__31`. Unity's Mono refuses it with `MethodAccessException`, so the
  login button does nothing (`Player.log`: "Method ... is inaccessible from method ...RecoveryOfficialSdkLoginStart").
  This is in the publisher's own DLL, with or without RO3Emu (verified on the unpatched original).

## What the patch does (`OriginGuardPatch`, applied to `Happyengine.Network.dll`)

By role, not by the obfuscated names the publisher changes from release to release:

1. Every method of `RegionalBootstrap` becomes a pass-through (the first argument comes back, nothing for `void`), and its
   static constructor does nothing, so the signed-origins runtime never starts and the URLs stay the ones `ClientPatcher`
   already rewrote.
2. `SealedFallbackRuntime.Redact*` (log redaction of hosts) become pass-throughs, as they read that runtime.
3. A private member used by an unrelated type of the same DLL becomes assembly-private (the `MethodAccessException`).

If the guard is not in the DLL (older releases) nothing is changed. If a method of `RegionalBootstrap` has a shape the
patch does not know, `RO3Emu.exe check` / `join` fail with a clear message instead of writing a broken DLL.

This disables a protection of the publisher that restricts which servers the client talks to; it is applied only to the
player's own local copy of the client, like the rest of the patch, and `RO3Emu.exe restore` puts the original back.

## Verified

- On the original DLL from a release 140075 client: exactly one access violation in all of the client's DLLs, none after
  the patch; the five bootstrap methods are `ldarg.0; ret` / `ret`.
- `tests/Ro3Emu.Setup.Tests/OriginGuardPatchTests.cs` (synthetic module of the same shape).
- **Not yet verified:** a login to a server in the game itself with the patched release 140075 client.
