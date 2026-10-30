# Contributing

Bug reports are more useful than patches here, because the hard part of this tool
is diagnosis, not code. If you are reporting a delete that fails, please include
the output of:

```console
unlockkit-cli "path\to\thing" --json -v
```

and `UNLOCKKIT_DEBUG=1` in the environment for the scanner counters.

## Ground rules

- **No kernel driver.** The whole point of staying in user mode is that the tool
  needs no signing, cannot bugcheck a machine, and does not have to fight
  antivirus products for control.
- **No silent privilege escalation.** Taking ownership, rewriting ACLs, or
  scheduling a boot-time delete must stay explicit user actions. If a feature
  needs one of those, it needs a visible choice.
- **Do not extend the deny-list exceptions.** `SafetyPolicy` refuses handles in
  `csrss`, `winlogon`, `services`, `lsass` and the antimalware engine for
  reasons that do not change with a good argument.
- **Failure messages must name the cause.** `Delete failed` is a bug; `sharing
  violation with no closable user handle — lock held in kernel` is the product.

## Building and testing

```console
dotnet build -c Release
dotnet publish src/UnlockKit.UI/UnlockKit.UI.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -o dist/gui
```

For exercising the delete paths, you need a deliberate locker: open the target
with a handle that omits `FILE_SHARE_DELETE` and keep it open while you run the
tool. A small console program that loops `FileStream` opens on a folder is enough
and is how the current behaviour was validated.

## Pull requests

- Keep the engine (`src/UnlockKit`) free of UI references — it is also the CLI.
- New `Native` imports need the exact calling convention and `CharSet` spelled
  out; `CharSet.Ansi` on a `CreateFileW` call silently truncates paths at the
  first non-ASCII character.
- If you change a verdict string, update `docs/TROUBLESHOOTING.md` and its
  `docs/TROUBLESHOOTING.en.md` counterpart — the docs quote the verdicts verbatim.
